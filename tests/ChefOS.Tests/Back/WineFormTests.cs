using ChefOS.Back.Components.Pages.Wines;
using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Wines;

namespace ChefOS.Tests.Back;

public class WineFormTests
{
    private static WineDraft Draft(string? appellation = "Chablis", params int[] recipeIds) =>
        new(appellation, "Bourgogne", "France", WineColor.Blanc, "Chardonnay", "Robe.", "Nez.", "Bouche.", "Garde.", "10 °C", "Accords.", ["Sole"])
        {
            SuggestedPairingRecipeIds = recipeIds
        };

    [Fact]
    public void New_StartsEmptyAndRed()
    {
        var form = new WineForm();

        Assert.Null(form.Id);
        Assert.Equal(WineColor.Rouge, form.Color);
        Assert.False(form.DescriptionIsAiDraft);
        Assert.False(form.CanDraft);
    }

    [Fact]
    public void CanDraft_NeedsAName()
    {
        var form = new WineForm { Name = "  " };
        Assert.False(form.CanDraft);

        form.Name = "Chablis";
        Assert.True(form.CanDraft);
    }

    [Fact]
    public void From_CarriesEveryFieldOfTheWine()
    {
        var wine = new WineListItem
        {
            Id = 7, Name = "Pomerol", Producer = "Château X", Vintage = 2018, Appellation = "Pomerol", Region = "Bordeaux", Country = "France",
            Color = WineColor.Rouge, Grapes = "Merlot", Robe = "r", Nez = "n", Bouche = "b", Garde = "g", ServiceTemperature = "16 °C",
            GeneralPairings = "p", PersonalNote = "note", PriceBottle = 60m, PriceGlass = 9.5m, IsPublished = true, DescriptionIsAiDraft = true
        };

        var form = WineForm.From(wine, [3, 1]);

        Assert.Equal(7, form.Id);
        Assert.Equal("Pomerol", form.Name);
        Assert.Equal("Château X", form.Producer);
        Assert.Equal(2018, form.Vintage);
        Assert.Equal(60m, form.PriceBottle);
        Assert.Equal(9.5m, form.PriceGlass);
        Assert.True(form.IsPublished);
        Assert.True(form.DescriptionIsAiDraft);
        Assert.False(form.ConfirmAiDraftReviewed);
        Assert.Equal([1, 3], form.PairingRecipeIds.Order());
    }

    [Fact]
    public void ApplyDraft_FillsTheSheet_RaisesTheFlag_AndMergesThePairings()
    {
        var form = new WineForm { Name = "Chablis", Region = "?", PairingRecipeIds = [5], ConfirmAiDraftReviewed = true };

        form.ApplyDraft(Draft("Chablis premier cru", 5, 9));

        Assert.Equal("Chablis premier cru", form.Appellation);
        Assert.Equal("Bourgogne", form.Region);
        Assert.Equal("France", form.Country);
        Assert.Equal(WineColor.Blanc, form.Color);
        Assert.Equal("Chardonnay", form.Grapes);
        Assert.Equal("Robe.", form.Robe);
        Assert.Equal("Nez.", form.Nez);
        Assert.Equal("Bouche.", form.Bouche);
        Assert.Equal("Garde.", form.Garde);
        Assert.Equal("10 °C", form.ServiceTemperature);
        Assert.Equal("Accords.", form.GeneralPairings);
        Assert.Equal([5, 9], form.PairingRecipeIds.Order());
        Assert.True(form.DescriptionIsAiDraft);
        Assert.False(form.ConfirmAiDraftReviewed);
        Assert.Equal("Chablis", form.Name);
    }

    [Fact]
    public void ApplyDraft_WithoutAppellation_KeepsTheOneTyped()
    {
        var form = new WineForm { Name = "Vin", Appellation = "Médoc" };

        form.ApplyDraft(Draft(appellation: null));

        Assert.Equal("Médoc", form.Appellation);
    }

    [Fact]
    public void EditingATextByHand_DoesNotClearTheDraftFlag()
    {
        var form = new WineForm { Name = "Vin", PriceBottle = 30m };
        form.ApplyDraft(Draft());

        form.Robe = "Retouchée à la main.";

        Assert.True(form.DescriptionIsAiDraft);
        Assert.True(form.ToInput().DescriptionIsAiDraft);
        Assert.False(form.ToInput().ConfirmAiDraftReviewed);
    }

    [Fact]
    public void ToInput_ConfirmationIsOnlySentWhileTheDraftFlagIsOn()
    {
        var form = new WineForm { Name = "Vin", PriceBottle = 30m, ConfirmAiDraftReviewed = true };
        Assert.False(form.ToInput().ConfirmAiDraftReviewed);

        form.ApplyDraft(Draft());
        form.ConfirmAiDraftReviewed = true;
        var input = form.ToInput();

        Assert.True(input.DescriptionIsAiDraft);
        Assert.True(input.ConfirmAiDraftReviewed);
    }

    [Fact]
    public void ToInput_MapsEveryField()
    {
        var form = new WineForm
        {
            Id = 4, Name = "Pomerol", Producer = "P", Vintage = 2018, Appellation = "A", Region = "R", Country = "C", Color = WineColor.Doux,
            Grapes = "G", Robe = "r", Nez = "n", Bouche = "b", Garde = "g", ServiceTemperature = "t", GeneralPairings = "p", PersonalNote = "note",
            PriceBottle = 60.5m, PriceGlass = 0m, IsPublished = true, PairingRecipeIds = [9, 2]
        };

        var input = form.ToInput();

        Assert.Equal(4, input.Id);
        Assert.Equal("Pomerol", input.Name);
        Assert.Equal(2018, input.Vintage);
        Assert.Equal(WineColor.Doux, input.Color);
        Assert.Equal(60.5m, input.PriceBottle);
        Assert.Equal(0m, input.PriceGlass);
        Assert.True(input.IsPublished);
        Assert.Equal([2, 9], input.PairingRecipeIds);
        Assert.Equal("note", input.PersonalNote);
    }

    [Fact]
    public void ToInput_EmptyBottlePrice_IsRefusedRatherThanZero()
    {
        var form = new WineForm { Name = "Vin", PriceBottle = null };

        var ex = Assert.Throws<DomainValidationException>(() => form.ToInput());

        Assert.Equal("Le prix de la bouteille est obligatoire", ex.Message);
    }
}
