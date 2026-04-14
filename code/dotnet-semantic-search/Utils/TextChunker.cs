namespace MicrosoftExtensionsAiSample.Utils;

/// <summary>
/// Splits long text into overlapping segments so each fits under embedding model limits.
/// Uses a conservative character budget (not a model tokenizer) to avoid exceeding embedding model context.
/// </summary>
internal static class TextChunker
{
    /// <summary>~2–3k tokens typical for English prose; stays under common embedding model context limits.</summary>
    public const int DefaultMaxChunkChars = 1_000;

    public const int DefaultOverlapChars = 400;

    public static IReadOnlyList<string> SplitIntoChunks(
        string text,
        int maxChunkChars = DefaultMaxChunkChars,
        int overlapChars = DefaultOverlapChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        if (text.Length <= maxChunkChars)
        {
            return new[] { text };
        }

        overlapChars = Math.Clamp(overlapChars, 0, maxChunkChars / 2);
        var chunks = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var remaining = text.Length - start;
            if (remaining <= maxChunkChars)
            {
                chunks.Add(text[start..]);
                break;
            }

            var end = start + maxChunkChars;
            var splitAt = FindPreferredBreak(text, start, end);
            chunks.Add(text[start..splitAt]);

            var nextStart = splitAt - overlapChars;
            if (nextStart <= start)
            {
                nextStart = splitAt;
            }

            start = nextStart;
        }

        return chunks;
    }

    private static int FindPreferredBreak(string text, int start, int end)
    {
        var window = text.AsSpan(start, end - start);
        var relNewline = window.LastIndexOf('\n');
        if (relNewline > window.Length * 3 / 4)
        {
            return start + relNewline + 1;
        }

        var relPara = window.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (relPara >= 0 && relPara > window.Length / 2)
        {
            return start + relPara + 2;
        }

        var relDot = window.LastIndexOf(". ");
        if (relDot > window.Length / 2)
        {
            return start + relDot + 2;
        }

        var relSpace = window.LastIndexOf(' ');
        if (relSpace > window.Length / 2)
        {
            return start + relSpace + 1;
        }

        return end;
    }
}
