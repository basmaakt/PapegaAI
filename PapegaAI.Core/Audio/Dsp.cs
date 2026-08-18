using NAudio.Wave;

namespace Parrot.Audio;

/// <summary>Level metering and sample-rate conversion, shared by every
/// platform's capture implementation.</summary>
public static class Dsp
{
    public static float ComputeRms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        double sum = 0;
        foreach (float s in samples) sum += (double)s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>Resample mono float audio using NAudio's WDL resampler
    /// (proper low-pass filtering, unlike naive decimation).</summary>
    public static float[] Resample(float[] source, int fromRate, int toRate)
    {
        if (source.Length == 0) return source;
        if (fromRate == toRate) return source;

        var provider = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(
            new ArraySampleProvider(source, fromRate), toRate);

        var output = new List<float>(
            (int)((long)source.Length * toRate / fromRate) + 64);
        var chunk = new float[8192];
        int read;
        while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
            output.AddRange(chunk.Take(read));
        return output.ToArray();
    }

    sealed class ArraySampleProvider : ISampleProvider
    {
        readonly float[] data;
        int position;

        public ArraySampleProvider(float[] data, int sampleRate)
        {
            this.data = data;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, data.Length - position);
            Array.Copy(data, position, buffer, offset, available);
            position += available;
            return available;
        }
    }
}
