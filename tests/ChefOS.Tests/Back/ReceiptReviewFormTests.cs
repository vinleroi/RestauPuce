using ChefOS.Back.Components.Pages.Receipts;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class ReceiptReviewFormTests
{
    private const int Butter = 1;      // grammes, 9,20 €/kg
    private const int Eggs = 2;        // pièces, 0,30 €
    private const int Saffron = 3;     // grammes, sans prix
    private const int Beer = 4;        // millilitres, 3,60 €/l

    private static readonly Dictionary<int, Ingredient> Ingredients = new()
    {
        [Butter] = new Ingredient { Id = Butter, Name = "Beurre", BaseUnit = BaseUnit.Gram, CurrentPricePerBaseUnit = 0.0092m },
        [Eggs] = new Ingredient { Id = Eggs, Name = "Œufs", BaseUnit = BaseUnit.Piece, CurrentPricePerBaseUnit = 0.30m },
        [Saffron] = new Ingredient { Id = Saffron, Name = "Safran", BaseUnit = BaseUnit.Gram, CurrentPricePerBaseUnit = null },
        [Beer] = new Ingredient { Id = Beer, Name = "Bière brune", BaseUnit = BaseUnit.Milliliter, CurrentPricePerBaseUnit = 0.0036m }
    };

    private static ReceiptLine StoredLine(int id, string label, decimal? quantity, MeasureUnit? unit, decimal? totalExclVat, int? ingredientId = null,
        MatchSource? source = null, decimal? vatRate = 0.06m, bool vatToCheck = false, bool ignored = false) => new()
    {
        Id = id,
        Position = id,
        RawLabel = label,
        Quantity = quantity,
        Unit = unit,
        UnitPrice = 99m,                // ce que le lecteur avait lu : le formulaire ne le reprend pas
        LineTotal = 99m,
        LineTotalExclVat = totalExclVat,
        VatRate = vatRate,
        VatToCheck = vatToCheck,
        IngredientId = ingredientId,
        MatchSource = source,
        IsIgnored = ignored
    };

    private static Receipt StoredReceipt(params ReceiptLine[] lines) => new()
    {
        Id = 12,
        SupplierId = 5,
        SupplierNameRaw = "Metro Cash & Carry",
        ReceiptDate = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc),
        InvoiceNumber = "F-123",
        AmountsIncludeVat = false,
        Total = 160.10m,
        Status = ReceiptStatus.Extracted,
        Lines = [.. lines]
    };

    private static ReceiptReviewForm FormWith(params ReceiptLine[] lines) => ReceiptReviewForm.From(StoredReceipt(lines));

    private static ReceiptLineEvaluation EvaluateSingle(ReceiptReviewLine line)
    {
        var form = ReceiptReviewForm.From(StoredReceipt());
        form.Lines.Add(line);
        return Assert.Single(form.Evaluate(Ingredients));
    }

    // ── From ─────────────────────────────────────────────────────────────────

    [Fact]
    public void From_CopiesTheHeader()
    {
        var form = FormWith();

        Assert.Equal(12, form.ReceiptId);
        Assert.Equal(5, form.SupplierId);
        Assert.Equal(new DateTime(2026, 11, 3), form.ReceiptDate);
        Assert.Equal("F-123", form.InvoiceNumber);
    }

    [Fact]
    public void From_CopiesTheEditableFieldsOfEachLine_InOrder()
    {
        var form = FormWith(
            StoredLine(7, "BEURRE DOUX 82% 1KG", 5m, MeasureUnit.Kg, 50.60m, Butter, MatchSource.SupplierAlias),
            StoredLine(8, "SACS POUBELLE 100L", 1m, MeasureUnit.Piece, 12.40m, null, ignored: true));

        Assert.Equal(2, form.Lines.Count);

        var butter = form.Lines[0];
        Assert.Equal(7, butter.Id);
        Assert.Equal("BEURRE DOUX 82% 1KG", butter.RawLabel);
        Assert.Equal(5m, butter.Quantity);
        Assert.Equal(MeasureUnit.Kg, butter.Unit);
        Assert.Equal(50.60m, butter.LineTotalExclVat);
        Assert.Equal(0.06m, butter.VatRate);
        Assert.Equal(Butter, butter.IngredientId);
        Assert.False(butter.IsIgnored);

        var bags = form.Lines[1];
        Assert.Equal(8, bags.Id);
        Assert.Null(bags.IngredientId);
        Assert.True(bags.IsIgnored);
    }

    // ── ToDraftInput ─────────────────────────────────────────────────────────

    [Fact]
    public void ToDraftInput_CarriesTheHeader()
    {
        var form = FormWith();
        form.SupplierId = 9;
        form.ReceiptDate = new DateTime(2026, 10, 30);
        form.InvoiceNumber = "F-999";

        var input = form.ToDraftInput();

        Assert.Equal(12, input.ReceiptId);
        Assert.Equal(9, input.SupplierId);
        Assert.Equal(new DateTime(2026, 10, 30), input.ReceiptDate);
        Assert.Equal("F-999", input.InvoiceNumber);
        Assert.Empty(input.Lines);
    }

    [Fact]
    public void ToDraftInput_MapsEveryLine_KeepingIdsAndOrder()
    {
        var form = FormWith(
            StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter, vatRate: 0.06m),
            StoredLine(8, "SACS POUBELLE", 1m, MeasureUnit.Piece, 12.40m, null, vatRate: 0.21m, ignored: true));

        var lines = form.ToDraftInput().Lines;

        Assert.Equal(new ReceiptLineInput(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, 0.06m, Butter, false), lines[0]);
        Assert.Equal(new ReceiptLineInput(8, "SACS POUBELLE", 1m, MeasureUnit.Piece, 12.40m, 0.21m, null, true), lines[1]);
    }

    [Fact]
    public void ToDraftInput_ReflectsTheEdits()
    {
        var form = FormWith(StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter));
        var line = form.Lines[0];
        line.RawLabel = "BEURRE DOUX";
        line.Quantity = 4.5m;
        line.Unit = MeasureUnit.G;
        line.LineTotalExclVat = 45m;
        line.IngredientId = Saffron;
        line.IsIgnored = true;

        var input = Assert.Single(form.ToDraftInput().Lines);

        Assert.Equal(new ReceiptLineInput(7, "BEURRE DOUX", 4.5m, MeasureUnit.G, 45m, 0.06m, Saffron, true), input);
    }

    [Fact]
    public void ToDraftInput_ALineAddedByHand_HasNoId_AndTheRemovedOnesAreAbsent()
    {
        var form = FormWith(
            StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter),
            StoredLine(8, "SACS", 1m, MeasureUnit.Piece, 12.40m));
        form.Lines.RemoveAt(1);
        form.Lines.Add(new ReceiptReviewLine { RawLabel = "OEUFS", Quantity = 30m, Unit = MeasureUnit.Piece, LineTotalExclVat = 8.70m, IngredientId = Eggs });

        var lines = form.ToDraftInput().Lines;

        Assert.Equal(2, lines.Count);
        Assert.Equal(7, lines[0].Id);
        Assert.Null(lines[1].Id);
        Assert.Equal("OEUFS", lines[1].RawLabel);
        Assert.Null(lines[1].VatRate);
        Assert.False(lines[1].IsIgnored);
    }

    // ── Evaluate : prix calculé, ancien prix, variation ──────────────────────

    [Fact]
    public void Evaluate_GivesThePricePerBaseUnit_TheOldPrice_AndTheVariation()
    {
        // 5 kg de beurre pour 50,60 € = 10,12 €/kg, contre 9,20 €/kg : +10 %.
        var form = FormWith(StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter));

        var result = Assert.Single(form.Evaluate(Ingredients));

        Assert.Equal(ReceiptLineIssue.None, result.Issue);
        Assert.Equal(0.01012m, result.PricePerBaseUnit);
        Assert.Equal(BaseUnit.Gram, result.BaseUnit);
        Assert.Equal(0.0092m, result.OldPricePerBaseUnit);
        Assert.Equal(0.1m, result.Variation);
    }

    [Fact]
    public void Evaluate_ADecrease_HasANegativeVariation()
    {
        // 30 œufs pour 8,10 € = 0,27 € pièce, contre 0,30 € : -10 %.
        var form = FormWith(StoredLine(7, "OEUFS", 30m, MeasureUnit.Piece, 8.10m, Eggs));

        Assert.Equal(-0.1m, Assert.Single(form.Evaluate(Ingredients)).Variation);
    }

    [Fact]
    public void Evaluate_DoesNotRoundTheVariation()
    {
        // 6 l de bière pour 21,90 € = 3,65 €/l, contre 3,60 €/l : +1,3888… %.
        var form = FormWith(StoredLine(7, "BIERE", 6m, MeasureUnit.L, 21.90m, Beer));

        var variation = Assert.Single(form.Evaluate(Ingredients)).Variation;

        Assert.Equal((0.00365m - 0.0036m) / 0.0036m, variation);
    }

    [Fact]
    public void Evaluate_IngredientWithoutPrice_HasThePriceButNoVariation()
    {
        var form = FormWith(StoredLine(7, "SAFRAN", 10m, MeasureUnit.G, 25m, Saffron));

        var result = Assert.Single(form.Evaluate(Ingredients));

        Assert.Equal(ReceiptLineIssue.None, result.Issue);
        Assert.Equal(2.5m, result.PricePerBaseUnit);
        Assert.Null(result.OldPricePerBaseUnit);
        Assert.Null(result.Variation);
    }

    [Fact]
    public void Evaluate_IngredientWithAZeroPrice_HasNoVariation_AndDoesNotDivideByZero()
    {
        var free = new Dictionary<int, Ingredient>
        {
            [Butter] = new Ingredient { Id = Butter, Name = "Beurre", BaseUnit = BaseUnit.Gram, CurrentPricePerBaseUnit = 0m }
        };
        var form = FormWith(StoredLine(7, "BEURRE", 1m, MeasureUnit.Kg, 9m, Butter));

        var result = Assert.Single(form.Evaluate(free));

        Assert.Equal(0.009m, result.PricePerBaseUnit);
        Assert.Null(result.Variation);
    }

    [Fact]
    public void Evaluate_ReturnsOneResultPerLine_InTheOrderOfTheLines()
    {
        var form = FormWith(
            StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter),
            StoredLine(8, "SACS", 1m, MeasureUnit.Piece, 12.40m),
            StoredLine(9, "OEUFS", 30m, MeasureUnit.Piece, 8.70m, Eggs));

        var results = form.Evaluate(Ingredients);

        Assert.Equal(3, results.Count);
        Assert.Equal(ReceiptLineIssue.None, results[0].Issue);
        Assert.Equal(ReceiptLineIssue.MissingIngredient, results[1].Issue);
        Assert.Equal(0.29m, results[2].PricePerBaseUnit);
    }

    [Fact]
    public void Evaluate_ReflectsTheEditsWithoutReloadingAnything()
    {
        var form = FormWith(StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter));
        Assert.Equal(0.1m, Assert.Single(form.Evaluate(Ingredients)).Variation);

        form.Lines[0].LineTotalExclVat = 46m;           // 9,20 €/kg : plus de variation

        Assert.Equal(0m, Assert.Single(form.Evaluate(Ingredients)).Variation);
    }

    // ── Evaluate : problèmes par ligne ───────────────────────────────────────

    [Fact]
    public void Evaluate_WithoutIngredient_IsMissingIngredient()
    {
        var result = EvaluateSingle(new ReceiptReviewLine { RawLabel = "X", Quantity = 1m, Unit = MeasureUnit.Kg, LineTotalExclVat = 5m });

        Assert.Equal(ReceiptLineIssue.MissingIngredient, result.Issue);
        Assert.Null(result.PricePerBaseUnit);
        Assert.Null(result.Variation);
    }

    [Fact]
    public void Evaluate_WithAnIngredientThatIsNoLongerThere_IsMissingIngredient()
    {
        var result = EvaluateSingle(new ReceiptReviewLine { Quantity = 1m, Unit = MeasureUnit.Kg, LineTotalExclVat = 5m, IngredientId = 999 });

        Assert.Equal(ReceiptLineIssue.MissingIngredient, result.Issue);
    }

    [Theory]
    [InlineData(null, MeasureUnit.Kg)]
    [InlineData(0.0, MeasureUnit.Kg)]
    [InlineData(-2.0, MeasureUnit.Kg)]
    [InlineData(2.0, null)]
    public void Evaluate_WithoutAUsableQuantityOrUnit_IsMissingQuantity(double? quantity, MeasureUnit? unit)
    {
        var result = EvaluateSingle(new ReceiptReviewLine
        {
            Quantity = quantity is { } q ? (decimal)q : null,
            Unit = unit,
            LineTotalExclVat = 5m,
            IngredientId = Butter
        });

        Assert.Equal(ReceiptLineIssue.MissingQuantity, result.Issue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void Evaluate_WithoutAPositiveTotal_IsInvalidTotal(double? total)
    {
        var result = EvaluateSingle(new ReceiptReviewLine
        {
            Quantity = 1m,
            Unit = MeasureUnit.Kg,
            LineTotalExclVat = total is { } t ? (decimal)t : null,
            IngredientId = Butter
        });

        Assert.Equal(ReceiptLineIssue.InvalidTotal, result.Issue);
    }

    [Fact]
    public void Evaluate_AUnitThatCannotBeConverted_IsIncompatibleUnit()
    {
        // Des pièces de beurre (en grammes) sans poids moyen, des litres d'œufs…
        var pieces = EvaluateSingle(new ReceiptReviewLine { Quantity = 3m, Unit = MeasureUnit.Piece, LineTotalExclVat = 5m, IngredientId = Butter });
        var litres = EvaluateSingle(new ReceiptReviewLine { Quantity = 3m, Unit = MeasureUnit.L, LineTotalExclVat = 5m, IngredientId = Eggs });

        Assert.Equal(ReceiptLineIssue.IncompatibleUnit, pieces.Issue);
        Assert.Equal(ReceiptLineIssue.IncompatibleUnit, litres.Issue);
        Assert.Null(pieces.PricePerBaseUnit);
    }

    [Fact]
    public void Evaluate_AnIgnoredLine_HasNoIssueAndNoPrice_EvenWhenIncomplete()
    {
        var result = EvaluateSingle(new ReceiptReviewLine { RawLabel = "SACS POUBELLE", IsIgnored = true });

        Assert.Equal(ReceiptLineIssue.None, result.Issue);
        Assert.Null(result.PricePerBaseUnit);
        Assert.Null(result.Variation);
    }

    [Fact]
    public void BlockingIssueCount_CountsTheLinesThatWouldRefuseTheApplication()
    {
        var form = FormWith(
            StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter),
            StoredLine(8, "SACS", 1m, MeasureUnit.Piece, 12.40m),
            StoredLine(9, "ANCIEN", null, null, null, ignored: true),
            StoredLine(10, "OEUFS", null, MeasureUnit.Piece, 8.70m, Eggs));

        Assert.Equal(2, ReceiptReviewForm.BlockingIssueCount(form.Evaluate(Ingredients)));
    }

    [Fact]
    public void ActiveLineCount_ExcludesTheIgnoredLines()
    {
        var form = FormWith(
            StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter),
            StoredLine(8, "SACS", 1m, MeasureUnit.Piece, 12.40m, ignored: true));

        Assert.Equal(1, form.ActiveLineCount);
    }

    // ── Variation : seuils de couleur ────────────────────────────────────────

    [Theory]
    [InlineData(0.0501, VariationLevel.Increase)]
    [InlineData(0.10, VariationLevel.Increase)]
    [InlineData(0.05, VariationLevel.Stable)]          // au-delà de +5 % : 5 % pile reste gris
    [InlineData(0.02, VariationLevel.Stable)]
    [InlineData(0, VariationLevel.Stable)]
    [InlineData(-0.02, VariationLevel.Stable)]
    [InlineData(-0.05, VariationLevel.Stable)]
    [InlineData(-0.0501, VariationLevel.Decrease)]
    [InlineData(-0.30, VariationLevel.Decrease)]
    public void LevelOf_IsRedAboveFivePercent_GreenBelowMinusFivePercent_GreyOtherwise(double variation, VariationLevel expected) =>
        Assert.Equal(expected, ReceiptReviewForm.LevelOf((decimal)variation));

    [Theory]
    [InlineData("0.0092", "0.01012", "0.1")]
    [InlineData("0.30", "0.27", "-0.1")]
    [InlineData("2", "2", "0")]
    public void Variation_IsNewMinusOldOverOld(string oldPrice, string newPrice, string expected) =>
        Assert.Equal(Invariant(expected), ReceiptReviewForm.Variation(Invariant(oldPrice), Invariant(newPrice)));

    [Theory]
    [InlineData(null, "1")]
    [InlineData("1", null)]
    [InlineData("0", "1")]
    [InlineData("-1", "1")]
    public void Variation_WithoutAUsableOldOrNewPrice_IsNull(string? oldPrice, string? newPrice) =>
        Assert.Null(ReceiptReviewForm.Variation(oldPrice is null ? null : Invariant(oldPrice), newPrice is null ? null : Invariant(newPrice)));

    // ── Origine de l'ingrédient et TVA à vérifier ────────────────────────────

    [Theory]
    [InlineData(MatchSource.NameMatch)]
    [InlineData(MatchSource.SupplierAlias)]
    [InlineData(MatchSource.GlobalAlias)]
    public void MatchSource_IsKeptWhileTheIngredientIsTheOneThatWasProposed(MatchSource source)
    {
        var form = FormWith(StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter, source));

        Assert.Equal(source, form.Lines[0].MatchSource);
    }

    [Fact]
    public void MatchSource_DisappearsAsSoonAsTheChefPicksAnotherIngredient_AndComesBackIfHeGoesBack()
    {
        var form = FormWith(StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter, MatchSource.NameMatch));
        var line = form.Lines[0];

        line.IngredientId = Saffron;
        Assert.Null(line.MatchSource);

        line.IngredientId = null;
        Assert.Null(line.MatchSource);

        line.IngredientId = Butter;
        Assert.Equal(MatchSource.NameMatch, line.MatchSource);
    }

    [Fact]
    public void MatchSource_OfALineAddedByHand_IsNull()
    {
        var line = new ReceiptReviewLine { IngredientId = Butter };

        Assert.Null(line.MatchSource);
    }

    [Fact]
    public void VatToCheck_IsKeptUntilTheChefChangesTheTotal()
    {
        var form = FormWith(StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 53.64m, Butter, vatRate: null, vatToCheck: true));
        var line = form.Lines[0];
        Assert.True(line.VatToCheck);

        line.LineTotalExclVat = 50.60m;
        Assert.False(line.VatToCheck);

        line.LineTotalExclVat = 53.64m;
        Assert.True(line.VatToCheck);
    }

    [Fact]
    public void VatToCheck_ComparesTheTotalRoundedToTheCent_LikeTheService()
    {
        var form = FormWith(StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 53.64m, Butter, vatRate: null, vatToCheck: true));
        form.Lines[0].LineTotalExclVat = 53.6400001m;

        Assert.True(form.Lines[0].VatToCheck);
    }

    [Fact]
    public void VatToCheck_IsFalseForALineThatNeverHadIt()
    {
        var form = FormWith(StoredLine(7, "BEURRE", 5m, MeasureUnit.Kg, 50.60m, Butter));

        Assert.False(form.Lines[0].VatToCheck);
        Assert.False(new ReceiptReviewLine().VatToCheck);
    }

    private static decimal Invariant(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
