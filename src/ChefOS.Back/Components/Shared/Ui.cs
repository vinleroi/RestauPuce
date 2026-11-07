using System.Globalization;
using ChefOS.Core.Costing;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Core.Vitrine;

namespace ChefOS.Back.Components.Shared;

/// <summary>Apparence d'une pastille de food cost : emoji devant le texte, couleur et variante MudBlazor, infobulle.</summary>
public sealed record FoodCostChipStyle(string Emoji, MudBlazor.Color Color, MudBlazor.Variant Variant, string Title);

/// <summary>
/// Culture et formats d'affichage communs aux pages du back : montants « 12,50 € », pourcentages « 87,5 % »,
/// libellés français des unités et des allergènes. Les arrondis ne servent qu'à l'affichage : les valeurs stockées restent exactes.
/// </summary>
public static class Ui
{
    private const string NoValue = "—";

    /// <summary>
    /// Culture de saisie et d'affichage. <c>MudNumericField</c> prend la culture invariante par défaut (virgule refusée) :
    /// chaque champ numérique doit recevoir <c>Culture="Ui.Fr"</c>.
    /// </summary>
    public static CultureInfo Fr { get; } = CultureInfo.GetCultureInfo("fr-BE");

    /// <summary>« 12,50 € ».</summary>
    public static string Money(decimal amount) => amount.ToString("N2", Fr) + " €";

    /// <summary>Montant signé (« +12,50 € » pour une perte, « -3,00 € » pour un gain) ; zéro, ou trop petit pour s'afficher, reste « 0,00 € ».</summary>
    public static string SignedMoney(decimal amount) =>
        Math.Round(amount, 2, MidpointRounding.AwayFromZero) switch
        {
            > 0m => "+" + Money(amount),
            0m => Money(0m),
            _ => Money(amount)
        };

    /// <summary>Pourcentage depuis une fraction (0,875 → « 87,5 % »), au plus deux décimales, sans zéro inutile.</summary>
    public static string Percent(decimal fraction) => (fraction * 100m).ToString("0.##", Fr) + " %";

    /// <summary>Pourcentage signé depuis une fraction (0,1 → « +10 % », -0,0625 → « -6,25 % ») ; zéro, ou trop petit pour s'afficher, reste « 0 % ».</summary>
    public static string SignedPercent(decimal fraction) =>
        Math.Round(fraction * 100m, 2, MidpointRounding.AwayFromZero) switch
        {
            > 0m => "+" + Percent(fraction),
            0m => Percent(0m),
            _ => Percent(fraction)
        };

    /// <summary>Variation d'un prix avec sa flèche : « ▲ +10 % », « ▼ -6,25 % » ; « 0 % » sans variation visible.</summary>
    public static string VariationText(decimal fraction) =>
        Math.Round(fraction * 100m, 2, MidpointRounding.AwayFromZero) switch
        {
            > 0m => "▲ " + SignedPercent(fraction),
            < 0m => "▼ " + SignedPercent(fraction),
            _ => SignedPercent(fraction)
        };

    /// <summary>Prix par unité d'affichage (« 12,50 € / kg »), converti depuis le prix par unité de base ; « — » sans prix.</summary>
    public static string PricePerDisplayUnit(decimal? pricePerBaseUnit, BaseUnit baseUnit) =>
        pricePerBaseUnit is { } price
            ? $"{Money(UnitConverter.PriceDisplayFromBase(price, baseUnit))} / {UnitConverter.DisplayUnitLabel(baseUnit)}"
            : NoValue;

    /// <summary>
    /// Coût par unité produite d'une préparation (« 0,50 € / l ») : coût total ÷ quantité produite (en unité de base),
    /// converti en unité d'affichage. « — » tant que la quantité produite manque ou n'est pas positive.
    /// </summary>
    public static string CostPerOutputUnit(decimal totalCost, decimal? outputQuantity, BaseUnit? outputUnit) =>
        outputQuantity is > 0m && outputUnit is { } unit
            ? PricePerDisplayUnit(totalCost / outputQuantity.Value, unit)
            : NoValue;

    /// <summary>
    /// Apparence d'une pastille de food cost, seul endroit où un niveau se traduit en emoji et en couleur :
    /// 🟢 sous 28 %, 🟠 jusqu'au seuil d'alerte, 🔴 au-delà, ⚠️ quand le coût n'est pas définitif.
    /// </summary>
    /// <param name="alertThreshold">Fraction au-delà de laquelle le plat est en alerte, rappelée dans l'infobulle de la pastille rouge.</param>
    public static FoodCostChipStyle FoodCostChipStyleFor(FoodCostLevel level, decimal alertThreshold) => level switch
    {
        FoodCostLevel.Good => new("🟢", MudBlazor.Color.Success, MudBlazor.Variant.Outlined, "Food cost maîtrisé"),
        FoodCostLevel.Warning => new("🟠", MudBlazor.Color.Warning, MudBlazor.Variant.Outlined, "Food cost à surveiller"),
        FoodCostLevel.Alert => new("🔴", MudBlazor.Color.Error, MudBlazor.Variant.Outlined, $"Au-dessus du seuil d'alerte ({Percent(alertThreshold)})"),
        FoodCostLevel.Incomplete => new("⚠️", MudBlazor.Color.Warning, MudBlazor.Variant.Filled, "Coût incomplet"),
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
    };

    /// <summary>Libellé court d'une unité de saisie : g, kg, ml, cl, l, pièce.</summary>
    public static string MeasureUnitLabel(MeasureUnit unit) => unit switch
    {
        MeasureUnit.G => "g",
        MeasureUnit.Kg => "kg",
        MeasureUnit.Ml => "ml",
        MeasureUnit.Cl => "cl",
        MeasureUnit.L => "l",
        MeasureUnit.Piece => "pièce",
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, null)
    };

    /// <summary>
    /// Unité proposée d'office pour compter ou jeter un produit : celle qu'on pèse ou qu'on verse (kg pour un ingrédient en grammes,
    /// l pour un liquide en millilitres, pièce pour un produit à la pièce).
    /// </summary>
    public static MeasureUnit DefaultMeasureUnit(BaseUnit baseUnit) => baseUnit switch
    {
        BaseUnit.Gram => MeasureUnit.Kg,
        BaseUnit.Milliliter => MeasureUnit.L,
        BaseUnit.Piece => MeasureUnit.Piece,
        _ => throw new ArgumentOutOfRangeException(nameof(baseUnit), baseUnit, null)
    };

    // Du plus gros au plus petit : l'ordre des listes d'unités proposées.
    private static readonly MeasureUnit[] UnitDisplayOrder =
        [MeasureUnit.Kg, MeasureUnit.G, MeasureUnit.L, MeasureUnit.Cl, MeasureUnit.Ml, MeasureUnit.Piece];

    /// <summary>
    /// Les unités de saisie qui se convertissent dans <paramref name="baseUnit"/> (le poids moyen d'une pièce ouvre le passage entre pièces et grammes),
    /// l'unité d'office en premier puis les autres du plus gros au plus petit : la liste d'un sélecteur d'unité qui ne propose que des unités acceptées.
    /// </summary>
    public static IReadOnlyList<MeasureUnit> ConvertibleUnits(BaseUnit baseUnit, decimal? averagePieceWeightGrams)
    {
        var preferred = DefaultMeasureUnit(baseUnit);
        return
        [
            preferred,
            .. UnitDisplayOrder.Where(unit => unit != preferred && UnitConverter.ToBase(1m, unit, baseUnit, averagePieceWeightGrams).Success)
        ];
    }

    /// <summary>
    /// « 2,5 kg », « 200 g », « 1 pièce », « 3 pièces » : la quantité (jusqu'à 4 décimales, sans zéro inutile) puis son unité.
    /// <paramref name="portions"/> : une recette se compte en portions (« 3 portions ») là où un ingrédient se compte en pièces.
    /// </summary>
    public static string QuantityText(decimal quantity, MeasureUnit unit, bool portions = false)
    {
        var plural = quantity > 1m;
        var label = unit == MeasureUnit.Piece
            ? (portions ? "portion" : "pièce") + (plural ? "s" : "")
            : MeasureUnitLabel(unit);

        return $"{quantity.ToString("0.####", Fr)} {label}";
    }

    /// <summary>
    /// Une quantité en unité de base, lisible : « 800 g » puis « 1,5 kg » à partir d'un kilo, « 350 ml » puis « 2 l », « 12 pièces ». Arrondie à l'affichage
    /// (au gramme ou millilitre, à deux décimales pour kg et l, à une pour les pièces). Une quantité négative garde son signe.
    /// </summary>
    public static string BaseQuantityText(decimal quantity, BaseUnit baseUnit) => baseUnit switch
    {
        BaseUnit.Gram => ScaledQuantity(quantity, "g", "kg"),
        BaseUnit.Milliliter => ScaledQuantity(quantity, "ml", "l"),
        BaseUnit.Piece => PieceQuantity(quantity),
        _ => throw new ArgumentOutOfRangeException(nameof(baseUnit), baseUnit, null)
    };

    private static string ScaledQuantity(decimal quantity, string smallUnit, string largeUnit)
    {
        var small = Math.Round(quantity, 0, MidpointRounding.AwayFromZero);
        if (Math.Abs(small) >= 1000m)
            return Math.Round(quantity / 1000m, 2, MidpointRounding.AwayFromZero).ToString("0.##", Fr) + " " + largeUnit;

        // Un arrondi à zéro ne garde pas le signe de la quantité (« -0 »).
        return (small == 0m ? 0m : small).ToString("0", Fr) + " " + smallUnit;
    }

    private static string PieceQuantity(decimal quantity)
    {
        var rounded = Math.Round(quantity, 1, MidpointRounding.AwayFromZero);
        return (rounded == 0m ? 0m : rounded).ToString("0.#", Fr) + (Math.Abs(rounded) > 1m ? " pièces" : " pièce");
    }

    /// <summary>Unité de la quantité produite par une préparation : kg, l ou pièces (au pluriel : on produit plusieurs pièces).</summary>
    public static string OutputUnitLabel(MeasureUnit unit) => unit == MeasureUnit.Piece ? "pièces" : MeasureUnitLabel(unit);

    /// <summary>Libellé d'un problème de chiffrage, rédigé pour que le chef sache quoi corriger.</summary>
    public static string IssueLabel(CostIssueKind kind) => kind switch
    {
        CostIssueKind.MissingPrice => "Prix manquant",
        CostIssueKind.IncompatibleUnit => "Unité incompatible — renseignez le poids moyen d'une pièce",
        CostIssueKind.InvalidOutputQuantity => "Quantité produite manquante",
        CostIssueKind.UnknownItem => "Élément introuvable",
        CostIssueKind.Cycle => "Boucle de préparations",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>« Safran : Prix manquant » ; sans nom d'élément (élément inconnu), le libellé seul.</summary>
    public static string IssueText(CostIssue issue) =>
        string.IsNullOrEmpty(issue.ItemName) ? IssueLabel(issue.Kind) : $"{issue.ItemName} : {IssueLabel(issue.Kind)}";

    public static string BaseUnitLabel(BaseUnit baseUnit) => baseUnit switch
    {
        BaseUnit.Gram => "Gramme",
        BaseUnit.Milliliter => "Millilitre",
        BaseUnit.Piece => "Pièce",
        _ => throw new ArgumentOutOfRangeException(nameof(baseUnit), baseUnit, null)
    };

    /// <summary>Statut d'un ticket fournisseur, en français : En attente, Lu, Échec, Appliqué.</summary>
    public static string ReceiptStatusLabel(ReceiptStatus status) => status switch
    {
        ReceiptStatus.Pending => "En attente",
        ReceiptStatus.Extracted => "Lu",
        ReceiptStatus.Failed => "Échec",
        ReceiptStatus.Applied => "Appliqué",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static MudBlazor.Color ReceiptStatusColor(ReceiptStatus status) => status switch
    {
        ReceiptStatus.Pending => MudBlazor.Color.Default,
        ReceiptStatus.Extracted => MudBlazor.Color.Info,
        ReceiptStatus.Failed => MudBlazor.Color.Error,
        ReceiptStatus.Applied => MudBlazor.Color.Success,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    /// <summary>Problème bloquant d'une ligne de ticket, rédigé pour que le chef sache quoi corriger ; vide quand la ligne est bonne.</summary>
    public static string ReceiptIssueLabel(ReceiptLineIssue issue) => issue switch
    {
        ReceiptLineIssue.None => "",
        ReceiptLineIssue.MissingIngredient => "Ingrédient à choisir",
        ReceiptLineIssue.MissingQuantity => "Quantité ou unité manquante",
        ReceiptLineIssue.IncompatibleUnit => "Unité incompatible avec l'ingrédient",
        ReceiptLineIssue.InvalidTotal => "Montant invalide",
        _ => throw new ArgumentOutOfRangeException(nameof(issue), issue, null)
    };

    public static string WasteReasonLabel(WasteReason reason) => reason switch
    {
        WasteReason.Perime => "Périmé",
        WasteReason.Casse => "Casse",
        WasteReason.Erreur => "Erreur",
        WasteReason.Surproduction => "Surproduction",
        WasteReason.Autre => "Autre",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    public static string OrderSourceLabel(OrderSource source) => source switch
    {
        OrderSource.Salle => "Salle",
        OrderSource.Emporter => "À emporter",
        OrderSource.Telephone => "Téléphone",
        OrderSource.Vitrine => "Vitrine",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, null)
    };

    public static string OrderStatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.Nouvelle => "Nouvelle",
        OrderStatus.EnPreparation => "En préparation",
        OrderStatus.Prete => "Prête",
        OrderStatus.Servie => "Servie",
        OrderStatus.Annulee => "Annulée",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    /// <summary>Libellé du bouton qui fait passer une commande à l'étape suivante ; null quand il n'y en a plus (servie ou annulée).</summary>
    public static string? AdvanceLabel(OrderStatus status) => status switch
    {
        OrderStatus.Nouvelle => "Préparer",
        OrderStatus.EnPreparation => "Marquer prête",
        OrderStatus.Prete => "Servir",
        OrderStatus.Servie or OrderStatus.Annulee => null,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    /// <summary>
    /// L'heure d'une commande sur le tableau, en heure locale : « 19:40 » pour une commande du jour affiché (<paramref name="dayUtc"/>, un jour UTC). Une commande encore à
    /// traiter d'un jour précédent porte son jour : « hier 23:40 » si elle date de la veille (jour UTC, comme le tableau), sinon « 02/11 23:40 ».
    /// </summary>
    /// <param name="zone">Le fuseau d'affichage ; celui du serveur par défaut.</param>
    public static string OrderTimeText(DateTime createdAtUtc, DateTime dayUtc, TimeZoneInfo? zone = null)
    {
        var created = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(created, zone ?? TimeZoneInfo.Local);
        var time = local.ToString("HH:mm", Fr);

        if (created >= dayUtc.Date)
            return time;

        return created.Date == dayUtc.Date.AddDays(-1) ? $"hier {time}" : $"{local.ToString("dd/MM", Fr)} {time}";
    }

    /// <summary>Vrai quand la commande n'est pas du jour affiché (<paramref name="dayUtc"/>) : une commande encore à traiter d'un jour précédent.</summary>
    public static bool IsFromEarlierDay(DateTime createdAtUtc, DateTime dayUtc) => createdAtUtc < dayUtc.Date;

    public static string StockCountStatusLabel(StockCountStatus status) => status switch
    {
        StockCountStatus.Brouillon => "Brouillon",
        StockCountStatus.Cloture => "Clôturé",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static string MenuClassLabel(MenuClass menuClass) => menuClass switch
    {
        MenuClass.Etoile => "Étoile",
        MenuClass.VacheALait => "Vache à lait",
        MenuClass.Enigme => "Énigme",
        MenuClass.PoidsMort => "Poids mort",
        _ => throw new ArgumentOutOfRangeException(nameof(menuClass), menuClass, null)
    };

    /// <summary>Le conseil d'une ligne pour un quadrant du menu engineering.</summary>
    public static string MenuClassAdvice(MenuClass menuClass) => menuClass switch
    {
        MenuClass.Etoile => "À garder tel quel",
        MenuClass.VacheALait => "Revoir le coût ou le prix",
        MenuClass.Enigme => "À mettre en avant",
        MenuClass.PoidsMort => "À remplacer",
        _ => throw new ArgumentOutOfRangeException(nameof(menuClass), menuClass, null)
    };

    public static string MenuClassEmoji(MenuClass menuClass) => menuClass switch
    {
        MenuClass.Etoile => "⭐",
        MenuClass.VacheALait => "🐄",
        MenuClass.Enigme => "🧩",
        MenuClass.PoidsMort => "⚓",
        _ => throw new ArgumentOutOfRangeException(nameof(menuClass), menuClass, null)
    };

    public static string ServiceLabel(ServicePeriod service) => service switch
    {
        ServicePeriod.Journee => "Journée",
        ServicePeriod.Midi => "Midi",
        ServicePeriod.Soir => "Soir",
        _ => throw new ArgumentOutOfRangeException(nameof(service), service, null)
    };

    public static string WineColorLabel(WineColor color) => color switch
    {
        WineColor.Rouge => "Rouge",
        WineColor.Blanc => "Blanc",
        WineColor.Rose => "Rosé",
        WineColor.Effervescent => "Effervescent",
        WineColor.Doux => "Doux",
        _ => throw new ArgumentOutOfRangeException(nameof(color), color, null)
    };

    /// <summary>Les 14 allergènes réglementaires de l'UE, un par valeur de l'énumération (hors <c>None</c>).</summary>
    public static IReadOnlyList<EuAllergen> Allergens { get; } =
        Enum.GetValues<EuAllergen>().Where(a => a != EuAllergen.None).ToArray();

    public static string AllergenLabel(EuAllergen allergen) => allergen switch
    {
        EuAllergen.Gluten => "Gluten",
        EuAllergen.Crustaceans => "Crustacés",
        EuAllergen.Eggs => "Œufs",
        EuAllergen.Fish => "Poisson",
        EuAllergen.Peanuts => "Arachides",
        EuAllergen.Soy => "Soja",
        EuAllergen.Milk => "Lait",
        EuAllergen.Nuts => "Fruits à coque",
        EuAllergen.Celery => "Céleri",
        EuAllergen.Mustard => "Moutarde",
        EuAllergen.Sesame => "Sésame",
        EuAllergen.Sulphites => "Sulfites",
        EuAllergen.Lupin => "Lupin",
        EuAllergen.Molluscs => "Mollusques",
        _ => throw new ArgumentOutOfRangeException(nameof(allergen), allergen, "Un seul allergène est attendu.")
    };

    /// <summary>
    /// Écart entre deux graduations d'un axe vertical de graphique : MudChart impose un entier d'au moins 1 (20 par défaut,
    /// inutilisable pour des prix de quelques euros). Vise environ quatre intervalles sur l'étendue des valeurs, arrondis à 1, 2, 5 × 10ⁿ.
    /// </summary>
    public static int AxisTickStep(double min, double max)
    {
        var target = max > min ? (max - min) / 4 : max / 4;
        if (target <= 1)
            return 1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        var normalized = target / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;

        return (int)Math.Min(nice * magnitude, int.MaxValue);
    }

    /// <summary>Recherche de texte sans tenir compte de la casse ni des accents (« echalote » trouve « Échalotes »). Un terme vide correspond à tout.</summary>
    public static bool ContainsText(string text, string? term) =>
        string.IsNullOrWhiteSpace(term)
        || Fr.CompareInfo.IndexOf(text, term.Trim(), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}
