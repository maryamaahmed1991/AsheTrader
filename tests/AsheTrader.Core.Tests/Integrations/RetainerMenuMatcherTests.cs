using AsheTrader.Workflow.Native;

namespace AsheTrader.Core.Tests.Integrations;

public sealed class RetainerMenuMatcherTests
{
    private const string ItemPrefix = "Entrust or withdraw items. (Slots filled: ";

    private static string?[] ObservedMenu(string filled) =>
    [
        $"Entrust or withdraw items. (Slots filled: {filled})",
        "Entrust or withdraw gil. (Gil entrusted: 0)",
        "Sell items in your inventory on the market.",
        "Sell items in your retainer's inventory on the market.",
        "View sale history.",
        "View venture report. (Complete)",
        "Assign venture. (In progress)",
        "View retainer attributes and gear.",
        "Reset retainer class. (Fisher: Lv. 100)",
        "Cast glamours.",
        "Dye items.",
        "Edit retainer comment.",
        "Quit.",
    ];

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("175")]
    public void RuntimeSlotCountDoesNotPreventSelectingItemStorage(string count)
    {
        var entries = ObservedMenu(count);

        Assert.Equal(0, RetainerMenuMatcher.FindUnique(entries, ItemPrefix, allowSuffix: true));
        Assert.Equal(12, RetainerMenuMatcher.FindUnique(entries, "Quit.", allowSuffix: false));
    }

    [Fact]
    public void ReportedTemplateMismatchRequiresLeadingPayloadInsteadOfEmptyParameterText()
    {
        var entries = ObservedMenu("0");
        const string extractedTemplate = "Entrust or withdraw items. (Slots filled: )";

        Assert.Equal(-1, RetainerMenuMatcher.FindUnique(entries, extractedTemplate, allowSuffix: false));
        Assert.Equal(-1, RetainerMenuMatcher.FindUnique(entries, extractedTemplate, allowSuffix: true));
        Assert.Equal(0, RetainerMenuMatcher.FindUnique(entries, ItemPrefix, allowSuffix: true));
    }

    [Theory]
    [InlineData("Objekte lagern. (Belegt: ", "Objekte lagern. (Belegt: 12)")]
    [InlineData("Ranger des objets. (Places : ", "Ranger des objets. (Places : 12)")]
    [InlineData("アイテムを預ける（使用枠：", "アイテムを預ける（使用枠：12）")]
    public void MatchingUsesSuppliedLocalizedTextWithoutEnglishOrNumberRules(string prefix, string entry)
    {
        Assert.Equal(1, RetainerMenuMatcher.FindUnique(["Unrelated action", entry, "Quit."], prefix, allowSuffix: true));
    }

    [Fact]
    public void StorageChoiceDoesNotDependOnMenuPosition()
    {
        var entries = ObservedMenu("42").Reverse().ToArray();

        Assert.Equal(12, RetainerMenuMatcher.FindUnique(entries, ItemPrefix, allowSuffix: true));
    }

    [Fact]
    public void StaticQuitRequiresExactTextEvenWhenSimilarActionsExist()
    {
        Assert.Equal(1, RetainerMenuMatcher.FindUnique(["Quit. More options", " Quit. "], "Quit.", allowSuffix: false));
        Assert.Equal(-1, RetainerMenuMatcher.FindUnique(["Quit. More options"], "Quit.", allowSuffix: false));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    public void EmptyLocalizedActionCannotMatchEveryEntry(string expected)
    {
        Assert.Throws<InvalidOperationException>(() => RetainerMenuMatcher.FindUnique(ObservedMenu("0"), expected, allowSuffix: true));
    }

    [Fact]
    public void AmbiguousStorageChoicesAreRejectedInsteadOfChoosingTheFirst()
    {
        Assert.Throws<InvalidOperationException>(() => RetainerMenuMatcher.FindUnique(
            ["Entrust or withdraw items. (Slots filled: 0)", "Entrust or withdraw items. (Slots filled: 1)"],
            ItemPrefix, allowSuffix: true));
        Assert.Throws<InvalidOperationException>(() => RetainerMenuMatcher.FindUnique(["Quit.", "Quit."], "Quit.", allowSuffix: false));
    }

    [Fact]
    public void SimilarGilAndSellingOptionsAreNotMistakenForItemStorage()
    {
        Assert.Equal(-1, RetainerMenuMatcher.FindUnique(ObservedMenu("0").Skip(1).ToArray(), ItemPrefix, allowSuffix: true));
    }

    [Fact]
    public void ComparisonRemainsOrdinalAndCaseSensitive()
    {
        Assert.Equal(-1, RetainerMenuMatcher.FindUnique(["entrust or withdraw items. (Slots filled: 0)"], ItemPrefix, allowSuffix: true));
    }

    [Fact]
    public void NullAndBlankEntriesAreSkippedWhileOuterWhitespaceIsIgnored()
    {
        Assert.Equal(2, RetainerMenuMatcher.FindUnique([null, "  ", "  Entrust or withdraw items. (Slots filled: 0)  "],
            "  " + ItemPrefix, allowSuffix: true));
    }
}
