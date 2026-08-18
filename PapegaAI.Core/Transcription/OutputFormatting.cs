namespace Parrot.Transcription;

/// <summary>
/// The last step before a transcript is typed into someone else's text field.
/// Kept here rather than in each platform's daemon so Windows and Linux cannot
/// drift apart on something the user sees in every single dictation.
/// </summary>
public static class OutputFormatting
{
    /// <summary>
    /// Prepare the transcript for injection.
    ///
    /// Whisper hands back a trimmed sentence, so dictating twice in a row
    /// glues the two together ("…daarhoe gaat het"). A leading space fixes
    /// that for every dictation after the first, at the cost of one stray
    /// space when you dictate into an empty field — which is far easier to
    /// remove than a missing space is to insert mid-word.
    ///
    /// Deliberately unconditional rather than clever: PapegaAI cannot read
    /// the character before the cursor (no API on either platform gives that
    /// for another application's window), and a guess based on "did you
    /// dictate recently?" would be right most of the time and mystifying the
    /// rest. Predictable beats smart for something that happens all day.
    /// </summary>
    /// <param name="text">The sanitized transcript, as stored in the history.</param>
    /// <param name="leadingSpace">The user's "spatie ervoor" setting.</param>
    public static string ForInjection(string text, bool leadingSpace)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return leadingSpace ? " " + text : text;
    }

    /// <summary>Default for <c>leading_space</c> when the config says nothing:
    /// on, because running words together is the worse failure.</summary>
    public const bool LeadingSpaceByDefault = true;
}
