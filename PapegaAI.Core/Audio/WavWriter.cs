namespace Parrot.Audio;

/// <summary>Writes float mono samples as 16-bit PCM WAV (debug dumps).</summary>
public static class WavWriter
{
    public static void Write(float[] samples, int sampleRate, string path)
    {
        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        int dataSize = samples.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + dataSize);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);                      // fmt chunk size
        w.Write((short)1);                // PCM
        w.Write((short)1);                // mono
        w.Write(sampleRate);
        w.Write(sampleRate * 2);          // byte rate
        w.Write((short)2);                // block align
        w.Write((short)16);               // bits per sample
        w.Write("data"u8);
        w.Write(dataSize);

        foreach (float s in samples)
        {
            float clamped = Math.Clamp(s, -1f, 1f);
            w.Write((short)(clamped * 32767f));
        }
    }

    /// <summary>Read a 16-bit or 32-bit-float PCM WAV back as mono float at
    /// its native rate. Only what `transcribe &lt;file.wav&gt;` needs — no
    /// compressed formats, so no platform-specific codec is involved.</summary>
    public static (float[] Samples, int SampleRate) Read(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);

        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("not a WAV file");
        r.ReadInt32();
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("not a WAV file");

        int channels = 0, sampleRate = 0, bits = 0, format = 0;
        while (fs.Position < fs.Length)
        {
            string id = new(r.ReadChars(4));
            int size = r.ReadInt32();
            long next = fs.Position + size + (size % 2);

            if (id == "fmt ")
            {
                format = r.ReadInt16();
                channels = r.ReadInt16();
                sampleRate = r.ReadInt32();
                r.ReadInt32();            // byte rate
                r.ReadInt16();            // block align
                bits = r.ReadInt16();
            }
            else if (id == "data")
            {
                if (channels == 0) throw new InvalidDataException("WAV data before fmt chunk");
                byte[] raw = r.ReadBytes(size);
                return (ToMonoFloat(raw, channels, bits, format), sampleRate);
            }

            fs.Position = next;
        }
        throw new InvalidDataException("WAV file has no data chunk");
    }

    static float[] ToMonoFloat(byte[] raw, int channels, int bits, int format)
    {
        int bytesPerSample = bits / 8;
        int frames = raw.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                int offset = (f * channels + ch) * bytesPerSample;
                sum += (bits, format) switch
                {
                    (16, _) => BitConverter.ToInt16(raw, offset) / 32768f,
                    (32, 3) => BitConverter.ToSingle(raw, offset),   // IEEE float
                    (32, _) => BitConverter.ToInt32(raw, offset) / 2147483648f,
                    (8, _) => (raw[offset] - 128) / 128f,
                    _ => throw new InvalidDataException($"unsupported WAV format ({bits}-bit, tag {format})"),
                };
            }
            mono[f] = sum / channels;
        }
        return mono;
    }
}
