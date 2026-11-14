using System.Text.RegularExpressions;

namespace AsheTrader.Core;

/// <summary>Matches the localized additional-chambers name shared by multiple estate door IDs.</summary>
public static partial class FuelWorkshopEntranceName
{
    public static bool Matches(string? actual, string? localized)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(localized)) return false;
        actual = actual.Trim();
        localized = localized.Trim();
        if (string.Equals(actual, localized, StringComparison.OrdinalIgnoreCase)) return true;

        // Some localized sheet strings contain grammatical tags which are absent in
        // the live object name. Use the same two exact name forms as Lifestream.
        var withoutTags = GrammarTags().Replace(localized, string.Empty).Trim();
        return withoutTags.Length > 0 && string.Equals(actual, withoutTags, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\[[^\[\]]*\]")]
    private static partial Regex GrammarTags();
}
