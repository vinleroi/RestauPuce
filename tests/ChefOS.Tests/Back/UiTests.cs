using ChefOS.Back.Components.Shared;
using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Tests.Back;

public class UiTests
{
    [Fact]
    public void Fr_UsesCommaAsDecimalSeparator_SoFrenchEntryIsAccepted()
    {
        Assert.Equal(12.5m, decimal.Parse("12,50", Ui.Fr));
        Assert.Equal(87.5m, decimal.Parse("87,5", Ui.Fr));
    }

    [Theory]
    [InlineData(12.5, "12,50 €")]
    [InlineData(0.3, "0,30 €")]
    [InlineData(0, "0,00 €")]
    [InlineData(14.567, "14,57 €")]
    public void Money_FormatsWithTwoDecimalsAndEuroSign(double amount, string expected) =>
        Assert.Equal(expected, Ui.Money((decimal)amount));

    [Fact]
    public void Money_GroupsThousandsWithTheCultureSeparator() =>
        Assert.Equal($"1{Ui.Fr.NumberFormat.NumberGroupSeparator}234,50 €", Ui.Money(1234.5m));

    [Theory]
    [InlineData(0.875, "87,5 %")]
    [InlineData(1, "100 %")]
    [InlineData(0.12, "12 %")]
    [InlineData(0.2843, "28,43 %")]
    [InlineData(0, "0 %")]
    public void Percent_TakesAFractionAndTrimsUselessZeros(double fraction, string expected) =>
        Assert.Equal(expected, Ui.Percent((decimal)fraction));

    [Theory]
    [InlineData("0.0125", BaseUnit.Gram, "12,50 € / kg")]
    [InlineData("0.0036", BaseUnit.Milliliter, "3,60 € / l")]
    [InlineData("0.3", BaseUnit.Piece, "0,30 € / pièce")]
    public void PricePerDisplayUnit_ConvertsFromTheBaseUnit(string pricePerBase, BaseUnit unit, string expected) =>
        Assert.Equal(expected, Ui.PricePerDisplayUnit(decimal.Parse(pricePerBase, System.Globalization.CultureInfo.InvariantCulture), unit));

    [Fact]
    public void PricePerDisplayUnit_RoundsOnlyForDisplay() =>
        Assert.Equal("14,57 € / kg", Ui.PricePerDisplayUnit(0.0145678m, BaseUnit.Gram));

    [Fact]
    public void PricePerDisplayUnit_WithoutPrice_ShowsADash() =>
        Assert.Equal("—", Ui.PricePerDisplayUnit(null, BaseUnit.Gram));

    [Theory]
    [InlineData(BaseUnit.Gram, "Gramme")]
    [InlineData(BaseUnit.Milliliter, "Millilitre")]
    [InlineData(BaseUnit.Piece, "Pièce")]
    public void BaseUnitLabel_IsInFrench(BaseUnit unit, string expected) =>
        Assert.Equal(expected, Ui.BaseUnitLabel(unit));

    [Fact]
    public void Allergens_ListsTheFourteenEuAllergensInEnumOrder()
    {
        Assert.Equal(14, Ui.Allergens.Count);
        Assert.DoesNotContain(EuAllergen.None, Ui.Allergens);
        Assert.Equal(Ui.Allergens.OrderBy(a => (int)a), Ui.Allergens);
    }

    [Fact]
    public void AllergenLabel_GivesTheFrenchNameOfEachAllergen()
    {
        var labels = Ui.Allergens.Select(Ui.AllergenLabel).ToArray();

        Assert.Equal(
            ["Gluten", "Crustacés", "Œufs", "Poisson", "Arachides", "Soja", "Lait", "Fruits à coque", "Céleri", "Moutarde", "Sésame", "Sulfites", "Lupin", "Mollusques"],
            labels);
    }

    [Theory]
    [InlineData(EuAllergen.None)]
    [InlineData(EuAllergen.Milk | EuAllergen.Eggs)]
    public void AllergenLabel_RejectsNoneAndCombinations(EuAllergen value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Ui.AllergenLabel(value));

    [Theory]
    [InlineData("Échalotes", "echalo", true)]
    [InlineData("Échalotes", "ÉCHALOTES", true)]
    [InlineData("Crème fraîche liquide 35 %", "fraiche", true)]
    [InlineData("Carottes", "oignon", false)]
    [InlineData("Carottes", "", true)]
    [InlineData("Carottes", "   ", true)]
    [InlineData("Carottes", null, true)]
    [InlineData("Carottes", " carot ", true)]
    public void ContainsText_IgnoresCaseAndAccents(string text, string? term, bool expected) =>
        Assert.Equal(expected, Ui.ContainsText(text, term));

    [Theory]
    [InlineData(57.5, 64, 2)]    // étendue 6,5 : une graduation tous les 2 €
    [InlineData(12.5, 14, 1)]    // étendue 1,5 : le minimum accepté par MudChart
    [InlineData(0.26, 0.3, 1)]   // sous 1 € : le minimum accepté par MudChart
    [InlineData(10, 50, 10)]
    [InlineData(10, 90, 20)]
    [InlineData(14, 14, 5)]      // un seul prix distinct : 14 / 4 = 3,5, arrondi à 5
    [InlineData(40, 40, 10)]
    [InlineData(0, 0, 1)]
    public void AxisTickStep_AimsForAboutFourIntervals(double min, double max, int expected) =>
        Assert.Equal(expected, Ui.AxisTickStep(min, max));

    [Theory]
    [InlineData(CostIssueKind.MissingPrice, "Prix manquant")]
    [InlineData(CostIssueKind.IncompatibleUnit, "Unité incompatible — renseignez le poids moyen d'une pièce")]
    [InlineData(CostIssueKind.InvalidOutputQuantity, "Quantité produite manquante")]
    [InlineData(CostIssueKind.UnknownItem, "Élément introuvable")]
    [InlineData(CostIssueKind.Cycle, "Boucle de préparations")]
    public void IssueLabel_IsInFrench(CostIssueKind kind, string expected) =>
        Assert.Equal(expected, Ui.IssueLabel(kind));

    [Fact]
    public void IssueText_NamesTheItemThenTheProblem() =>
        Assert.Equal("Safran : Prix manquant", Ui.IssueText(new CostIssue(CostIssueKind.MissingPrice, "Safran", 2)));

    [Fact]
    public void IssueText_WithoutItemName_IsJustTheProblem() =>
        Assert.Equal("Élément introuvable", Ui.IssueText(new CostIssue(CostIssueKind.UnknownItem, "", null)));

    [Theory]
    [InlineData(MeasureUnit.G, "g")]
    [InlineData(MeasureUnit.Kg, "kg")]
    [InlineData(MeasureUnit.Ml, "ml")]
    [InlineData(MeasureUnit.Cl, "cl")]
    [InlineData(MeasureUnit.L, "l")]
    [InlineData(MeasureUnit.Piece, "pièce")]
    public void MeasureUnitLabel_IsTheUsualAbbreviation(MeasureUnit unit, string expected) =>
        Assert.Equal(expected, Ui.MeasureUnitLabel(unit));

    [Theory]
    [InlineData(MeasureUnit.Kg, "kg")]
    [InlineData(MeasureUnit.L, "l")]
    [InlineData(MeasureUnit.Piece, "pièces")]
    public void OutputUnitLabel_IsPluralForPieces(MeasureUnit unit, string expected) =>
        Assert.Equal(expected, Ui.OutputUnitLabel(unit));

    [Theory]
    [InlineData("1.00", "2000", BaseUnit.Milliliter, "0,50 € / l")]      // 1 € pour 2 l
    [InlineData("4.50", "450", BaseUnit.Gram, "10,00 € / kg")]           // 4,50 € pour 450 g
    [InlineData("3.00", "12", BaseUnit.Piece, "0,25 € / pièce")]
    public void CostPerOutputUnit_DividesTheCostByTheQuantityProduced(string cost, string output, BaseUnit unit, string expected) =>
        Assert.Equal(expected, Ui.CostPerOutputUnit(Invariant(cost), Invariant(output), unit));

    [Theory]
    [InlineData(null, BaseUnit.Gram)]
    [InlineData("0", BaseUnit.Gram)]
    [InlineData("-5", BaseUnit.Gram)]
    [InlineData("100", null)]
    public void CostPerOutputUnit_WithoutAUsableOutput_ShowsADash(string? output, BaseUnit? unit) =>
        Assert.Equal("—", Ui.CostPerOutputUnit(4m, output is null ? null : Invariant(output), unit));

    [Theory]
    [InlineData(FoodCostLevel.Good, "🟢", "Success", "Outlined", "Food cost maîtrisé")]
    [InlineData(FoodCostLevel.Warning, "🟠", "Warning", "Outlined", "Food cost à surveiller")]
    [InlineData(FoodCostLevel.Alert, "🔴", "Error", "Outlined", "Au-dessus du seuil d'alerte (35 %)")]
    [InlineData(FoodCostLevel.Incomplete, "⚠️", "Warning", "Filled", "Coût incomplet")]
    public void FoodCostChipStyleFor_MapsEachLevelToItsEmojiAndColour(FoodCostLevel level, string emoji, string color, string variant, string title)
    {
        var style = Ui.FoodCostChipStyleFor(level, 0.35m);

        Assert.Equal(emoji, style.Emoji);
        Assert.Equal(color, style.Color.ToString());
        Assert.Equal(variant, style.Variant.ToString());
        Assert.Equal(title, style.Title);
    }

    [Fact]
    public void FoodCostChipStyleFor_RecallsTheRestaurantThresholdOnTheAlertChip() =>
        Assert.Equal("Au-dessus du seuil d'alerte (32,5 %)", Ui.FoodCostChipStyleFor(FoodCostLevel.Alert, 0.325m).Title);

    [Fact]
    public void FoodCostChipStyleFor_CoversEveryLevel()
    {
        // Un niveau ajouté à l'énumération sans apparence correspondante lèverait une exception à l'affichage : on le détecte ici.
        foreach (var level in Enum.GetValues<FoodCostLevel>())
            Assert.False(string.IsNullOrEmpty(Ui.FoodCostChipStyleFor(level, 0.35m).Emoji));
    }

    [Theory]
    [InlineData(0.1, "+10 %")]
    [InlineData(0.0625, "+6,25 %")]
    [InlineData(-0.0625, "-6,25 %")]
    [InlineData(0, "0 %")]
    [InlineData(0.00001, "0 %")]       // trop petit pour s'afficher : ni « + » ni « -0 »
    [InlineData(-0.00001, "0 %")]
    public void SignedPercent_ShowsThePlusSign_AndNeverAMinusZero(double fraction, string expected) =>
        Assert.Equal(expected, Ui.SignedPercent((decimal)fraction));

    [Theory]
    [InlineData(0.1, "▲ +10 %")]
    [InlineData(-0.0625, "▼ -6,25 %")]
    [InlineData(0, "0 %")]
    [InlineData(-0.00001, "0 %")]
    public void VariationText_PutsAnArrowInFrontOfTheVisibleVariation(double fraction, string expected) =>
        Assert.Equal(expected, Ui.VariationText((decimal)fraction));

    [Theory]
    [InlineData(ReceiptStatus.Pending, "En attente", "Default")]
    [InlineData(ReceiptStatus.Extracted, "Lu", "Info")]
    [InlineData(ReceiptStatus.Failed, "Échec", "Error")]
    [InlineData(ReceiptStatus.Applied, "Appliqué", "Success")]
    public void ReceiptStatus_HasAFrenchLabelAndAColour(ReceiptStatus status, string label, string color)
    {
        Assert.Equal(label, Ui.ReceiptStatusLabel(status));
        Assert.Equal(color, Ui.ReceiptStatusColor(status).ToString());
    }

    [Theory]
    [InlineData(ReceiptLineIssue.None, "")]
    [InlineData(ReceiptLineIssue.MissingIngredient, "Ingrédient à choisir")]
    [InlineData(ReceiptLineIssue.MissingQuantity, "Quantité ou unité manquante")]
    [InlineData(ReceiptLineIssue.IncompatibleUnit, "Unité incompatible avec l'ingrédient")]
    [InlineData(ReceiptLineIssue.InvalidTotal, "Montant invalide")]
    public void ReceiptIssueLabel_IsInFrench(ReceiptLineIssue issue, string expected) =>
        Assert.Equal(expected, Ui.ReceiptIssueLabel(issue));

    // ── Unités de saisie ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(BaseUnit.Gram, MeasureUnit.Kg)]
    [InlineData(BaseUnit.Milliliter, MeasureUnit.L)]
    [InlineData(BaseUnit.Piece, MeasureUnit.Piece)]
    public void DefaultMeasureUnit_IsTheUnitAChefWouldWeighOrPourWith(BaseUnit baseUnit, MeasureUnit expected) =>
        Assert.Equal(expected, Ui.DefaultMeasureUnit(baseUnit));

    [Theory]
    [InlineData(BaseUnit.Gram, null, new[] { MeasureUnit.Kg, MeasureUnit.G })]
    [InlineData(BaseUnit.Gram, 80.0, new[] { MeasureUnit.Kg, MeasureUnit.G, MeasureUnit.Piece })]
    [InlineData(BaseUnit.Milliliter, null, new[] { MeasureUnit.L, MeasureUnit.Cl, MeasureUnit.Ml })]
    [InlineData(BaseUnit.Milliliter, 80.0, new[] { MeasureUnit.L, MeasureUnit.Cl, MeasureUnit.Ml })]
    [InlineData(BaseUnit.Piece, null, new[] { MeasureUnit.Piece })]
    [InlineData(BaseUnit.Piece, 60.0, new[] { MeasureUnit.Piece, MeasureUnit.Kg, MeasureUnit.G })]
    public void ConvertibleUnits_ListsOnlyUnitsThatConvertToTheBaseUnit_DefaultFirst(BaseUnit baseUnit, double? pieceWeight, MeasureUnit[] expected) =>
        Assert.Equal(expected, Ui.ConvertibleUnits(baseUnit, pieceWeight is { } weight ? (decimal)weight : null));

    [Theory]
    [InlineData(2.5, MeasureUnit.Kg, "2,5 kg")]
    [InlineData(200, MeasureUnit.G, "200 g")]
    [InlineData(0.3333, MeasureUnit.L, "0,3333 l")]
    [InlineData(1, MeasureUnit.Piece, "1 pièce")]
    [InlineData(0.5, MeasureUnit.Piece, "0,5 pièce")]
    [InlineData(3, MeasureUnit.Piece, "3 pièces")]
    public void QuantityText_UsesTheFrenchDecimalCommaAndPluralisesPieces(double quantity, MeasureUnit unit, string expected) =>
        Assert.Equal(expected, Ui.QuantityText((decimal)quantity, unit));

    [Theory]
    [InlineData(1, MeasureUnit.Piece, "1 portion")]
    [InlineData(0.5, MeasureUnit.Piece, "0,5 portion")]
    [InlineData(3, MeasureUnit.Piece, "3 portions")]
    [InlineData(2, MeasureUnit.Kg, "2 kg")]
    public void QuantityText_ForARecipe_CountsPortionsInsteadOfPieces(double quantity, MeasureUnit unit, string expected) =>
        Assert.Equal(expected, Ui.QuantityText((decimal)quantity, unit, portions: true));

    // ── Libellés des ventes, commandes, pertes et inventaires ────────────────

    [Theory]
    [InlineData(MenuClass.Etoile, "Étoile")]
    [InlineData(MenuClass.VacheALait, "Vache à lait")]
    [InlineData(MenuClass.Enigme, "Énigme")]
    [InlineData(MenuClass.PoidsMort, "Poids mort")]
    public void MenuClassLabel_IsInFrench(MenuClass menuClass, string expected) =>
        Assert.Equal(expected, Ui.MenuClassLabel(menuClass));

    [Theory]
    [InlineData(MenuClass.Etoile, "À garder tel quel")]
    [InlineData(MenuClass.VacheALait, "Revoir le coût ou le prix")]
    [InlineData(MenuClass.Enigme, "À mettre en avant")]
    [InlineData(MenuClass.PoidsMort, "À remplacer")]
    public void MenuClassAdvice_IsOneShortLinePerQuadrant(MenuClass menuClass, string expected) =>
        Assert.Equal(expected, Ui.MenuClassAdvice(menuClass));

    [Fact]
    public void MenuClass_EveryValueHasALabelAnAdviceAndAnEmoji()
    {
        foreach (var menuClass in Enum.GetValues<MenuClass>())
        {
            Assert.False(string.IsNullOrWhiteSpace(Ui.MenuClassLabel(menuClass)));
            Assert.False(string.IsNullOrWhiteSpace(Ui.MenuClassAdvice(menuClass)));
            Assert.False(string.IsNullOrWhiteSpace(Ui.MenuClassEmoji(menuClass)));
        }

        Assert.Equal(4, Enum.GetValues<MenuClass>().Select(Ui.MenuClassEmoji).Distinct().Count());
    }

    [Theory]
    [InlineData(1500, BaseUnit.Gram, "1,5 kg")]
    [InlineData(800, BaseUnit.Gram, "800 g")]
    [InlineData(999.6, BaseUnit.Gram, "1 kg")]
    [InlineData(1234.5678, BaseUnit.Gram, "1,23 kg")]
    [InlineData(0, BaseUnit.Gram, "0 g")]
    [InlineData(-250, BaseUnit.Gram, "-250 g")]
    [InlineData(-1250, BaseUnit.Gram, "-1,25 kg")]
    [InlineData(2000, BaseUnit.Milliliter, "2 l")]
    [InlineData(350, BaseUnit.Milliliter, "350 ml")]
    [InlineData(12, BaseUnit.Piece, "12 pièces")]
    [InlineData(1, BaseUnit.Piece, "1 pièce")]
    [InlineData(2.5, BaseUnit.Piece, "2,5 pièces")]
    [InlineData(0, BaseUnit.Piece, "0 pièce")]
    [InlineData(-3, BaseUnit.Piece, "-3 pièces")]
    public void BaseQuantityText_ShowsKilosAndLitresFromAThousand(double quantity, BaseUnit unit, string expected) =>
        Assert.Equal(expected, Ui.BaseQuantityText((decimal)quantity, unit));

    [Theory]
    [InlineData(12.5, "+12,50 €")]
    [InlineData(-3, "-3,00 €")]
    [InlineData(0, "0,00 €")]
    [InlineData(0.004, "0,00 €")]
    [InlineData(-0.004, "0,00 €")]
    [InlineData(0.005, "+0,01 €")]
    public void SignedMoney_ShowsThePlusSignOfALoss(double amount, string expected) =>
        Assert.Equal(expected, Ui.SignedMoney((decimal)amount));

    [Theory]
    [InlineData(WasteReason.Perime, "Périmé")]
    [InlineData(WasteReason.Casse, "Casse")]
    [InlineData(WasteReason.Erreur, "Erreur")]
    [InlineData(WasteReason.Surproduction, "Surproduction")]
    [InlineData(WasteReason.Autre, "Autre")]
    public void WasteReasonLabel_IsInFrench(WasteReason reason, string expected) =>
        Assert.Equal(expected, Ui.WasteReasonLabel(reason));

    [Theory]
    [InlineData(OrderSource.Salle, "Salle")]
    [InlineData(OrderSource.Emporter, "À emporter")]
    [InlineData(OrderSource.Telephone, "Téléphone")]
    [InlineData(OrderSource.Vitrine, "Vitrine")]
    public void OrderSourceLabel_IsInFrench(OrderSource source, string expected) =>
        Assert.Equal(expected, Ui.OrderSourceLabel(source));

    [Theory]
    [InlineData(OrderStatus.Nouvelle, "Nouvelle")]
    [InlineData(OrderStatus.EnPreparation, "En préparation")]
    [InlineData(OrderStatus.Prete, "Prête")]
    [InlineData(OrderStatus.Servie, "Servie")]
    [InlineData(OrderStatus.Annulee, "Annulée")]
    public void OrderStatusLabel_IsInFrench(OrderStatus status, string expected) =>
        Assert.Equal(expected, Ui.OrderStatusLabel(status));

    [Theory]
    [InlineData(OrderStatus.Nouvelle, "Préparer")]
    [InlineData(OrderStatus.EnPreparation, "Marquer prête")]
    [InlineData(OrderStatus.Prete, "Servir")]
    [InlineData(OrderStatus.Servie, null)]
    [InlineData(OrderStatus.Annulee, null)]
    public void AdvanceLabel_NamesTheNextStep_AndIsAbsentOnceTheOrderIsFinished(OrderStatus status, string? expected) =>
        Assert.Equal(expected, Ui.AdvanceLabel(status));

    // Bruxelles en novembre : UTC+1 (heure d'hiver), en octobre UTC+2 (heure d'été). Le jour affiché est un jour du restaurant.
    private static readonly RestaurantCalendar Brussels = new("Europe/Brussels", new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc));
    private static readonly DateTime Today = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(2026, 11, 2, 23, 0, "00:00")]      // minuit à Bruxelles : début du jour affiché
    [InlineData(2026, 11, 3, 18, 40, "19:40")]
    [InlineData(2026, 11, 3, 23, 59, "00:59")]
    public void OrderTimeText_OfTheDisplayedDay_IsJustTheLocalTime(int year, int month, int day, int hour, int minute, string expected) =>
        Assert.Equal(expected, Ui.OrderTimeText(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc), Today, Brussels));

    [Theory]
    [InlineData(2026, 11, 2, 22, 40, "hier 23:40")]
    [InlineData(2026, 11, 2, 0, 0, "hier 01:00")]
    [InlineData(2026, 11, 1, 23, 0, "hier 00:00")]
    public void OrderTimeText_OfTheDayBefore_SaysYesterday(int year, int month, int day, int hour, int minute, string expected) =>
        Assert.Equal(expected, Ui.OrderTimeText(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc), Today, Brussels));

    [Theory]
    [InlineData(2026, 11, 1, 22, 40, "01/11 23:40")]
    [InlineData(2026, 10, 12, 8, 5, "12/10 10:05")]    // heure d'été : UTC+2
    [InlineData(2026, 10, 31, 23, 30, "01/11 00:30")]   // le jour affiché est celui de l'heure locale
    [InlineData(2025, 12, 31, 12, 0, "31/12 13:00")]
    public void OrderTimeText_OfAnEarlierDay_ShowsTheDateAndTheTime(int year, int month, int day, int hour, int minute, string expected) =>
        Assert.Equal(expected, Ui.OrderTimeText(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc), Today, Brussels));

    [Fact]
    public void OrderTimeText_ReadsAnUnspecifiedCreationDateAsUtc() =>
        Assert.Equal("hier 23:40", Ui.OrderTimeText(new DateTime(2026, 11, 2, 22, 40, 0, DateTimeKind.Unspecified), Today, Brussels));

    [Fact]
    public void IsFromEarlierDay_IsTrueBeforeTheDisplayedRestaurantDayOnly()
    {
        // 22 h 59 UTC la veille : 23 h 59 à Bruxelles, encore la veille ; 23 h 00 UTC : minuit, le jour affiché commence.
        Assert.True(Ui.IsFromEarlierDay(new DateTime(2026, 11, 2, 22, 59, 0, DateTimeKind.Utc), Today, Brussels));
        Assert.False(Ui.IsFromEarlierDay(new DateTime(2026, 11, 2, 23, 0, 0, DateTimeKind.Utc), Today, Brussels));
        Assert.False(Ui.IsFromEarlierDay(Today.AddHours(15), Today.AddHours(15), Brussels));   // seul le jour du tableau compte, pas l'heure
    }

    [Theory]
    [InlineData(StockCountStatus.Brouillon, "Brouillon")]
    [InlineData(StockCountStatus.Cloture, "Clôturé")]
    public void StockCountStatusLabel_IsInFrench(StockCountStatus status, string expected) =>
        Assert.Equal(expected, Ui.StockCountStatusLabel(status));

    [Theory]
    [InlineData(ServicePeriod.Journee, "Journée")]
    [InlineData(ServicePeriod.Midi, "Midi")]
    [InlineData(ServicePeriod.Soir, "Soir")]
    public void ServiceLabel_IsInFrench(ServicePeriod service, string expected) =>
        Assert.Equal(expected, Ui.ServiceLabel(service));

    [Theory]
    [InlineData(WineColor.Rouge, "Rouge")]
    [InlineData(WineColor.Blanc, "Blanc")]
    [InlineData(WineColor.Rose, "Rosé")]
    [InlineData(WineColor.Effervescent, "Effervescent")]
    [InlineData(WineColor.Doux, "Doux")]
    public void WineColorLabel_IsInFrench(WineColor color, string expected) =>
        Assert.Equal(expected, Ui.WineColorLabel(color));

    private static decimal Invariant(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
