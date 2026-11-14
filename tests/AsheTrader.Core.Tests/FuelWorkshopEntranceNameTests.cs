using AsheTrader.Core;

namespace AsheTrader.Core.Tests;

public sealed class FuelWorkshopEntranceNameTests
{
    private const string EnglishDoor = "Entrance to Additional Chambers";

    [Theory]
    [InlineData("Entrance to Additional Chambers")]
    [InlineData("entrance to additional chambers")]
    [InlineData("  Entrance to Additional Chambers  ")]
    public void RecognizesTheSharedDoorNameIndependentOfCapitalization(string actual)
        => Assert.True(FuelWorkshopEntranceName.Matches(actual, EnglishDoor));

    // Installed SqPack EObjName 2004353 and renovated-interior variant 2011571
    // share these labels. Live object names can omit the German grammar tag.
    [Theory]
    [InlineData("entrance to additional chambers", "entrance to additional chambers")]
    [InlineData("Eingang[p] zu anderen Zimmern", "Eingang[p] zu anderen Zimmern")]
    [InlineData("Eingang zu anderen Zimmern", "Eingang[p] zu anderen Zimmern")]
    [InlineData("accès vers les autres pièces", "accès vers les autres pièces")]
    [InlineData("別室へ移動する", "別室へ移動する")]
    public void MatchesInstalledGameLabelsAcrossClientLanguages(string actual, string localized)
        => Assert.True(FuelWorkshopEntranceName.Matches(actual, localized));

    [Theory]
    [InlineData("Entrance")]
    [InlineData("Additional Chambers")]
    [InlineData("Entrance to Additional Chambers (Private)")]
    [InlineData("Exit from Entrance to Additional Chambers")]
    [InlineData("Company Workshop")]
    public void DoesNotSelectAnUnrelatedDoorBySubstring(string actual)
        => Assert.False(FuelWorkshopEntranceName.Matches(actual, EnglishDoor));

    [Theory]
    [InlineData(null, EnglishDoor)]
    [InlineData("", EnglishDoor)]
    [InlineData(" ", EnglishDoor)]
    [InlineData(EnglishDoor, null)]
    [InlineData(EnglishDoor, "")]
    [InlineData(EnglishDoor, " ")]
    [InlineData(" ", "[grammar]")]
    public void MissingNamesCannotAuthorizeAnInteraction(string? actual, string? localized)
        => Assert.False(FuelWorkshopEntranceName.Matches(actual, localized));

    [Fact]
    public void SupportsTheRawAndTagStrippedLocalizedForms()
    {
        const string localized = "[article]Entrance to Additional Chambers[grammar]";
        Assert.True(FuelWorkshopEntranceName.Matches(localized, localized));
        Assert.True(FuelWorkshopEntranceName.Matches(EnglishDoor, localized));
        Assert.False(FuelWorkshopEntranceName.Matches("[different]" + EnglishDoor, localized));
    }

    [Fact]
    public void DoesNotRemoveMeaningfulWordsOrPunctuationFromALiveName()
    {
        Assert.False(FuelWorkshopEntranceName.Matches(EnglishDoor + "[Private]", EnglishDoor));
        Assert.False(FuelWorkshopEntranceName.Matches(EnglishDoor + ".", EnglishDoor));
    }
}
