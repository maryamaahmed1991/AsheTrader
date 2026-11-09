namespace AsheTrader.Workflow.Native;

/// <summary>
/// Matches a localized retainer action without assuming a fixed menu position. Dynamic
/// rows use their leading text payload; static rows retain exact matching.
/// </summary>
internal static class RetainerMenuMatcher
{
    public static int FindUnique(IReadOnlyList<string?> entries, string expected, bool allowSuffix)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(expected))
            throw new InvalidOperationException("The localized retainer menu action is empty; no option was selected.");
        var text = expected.Trim();
        var match = -1;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index]?.Trim();
            if (string.IsNullOrEmpty(entry)) continue;
            var matches = allowSuffix
                ? entry.StartsWith(text, StringComparison.Ordinal)
                : string.Equals(entry, text, StringComparison.Ordinal);
            if (!matches) continue;
            if (match >= 0)
                throw new InvalidOperationException("More than one retainer menu option matches the requested action; no option was selected.");
            match = index;
        }
        return match;
    }
}
