using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Receipts;

/// <summary>Couleur de la variation d'un prix : rouge (hausse notable), vert (baisse notable), gris (stable).</summary>
public enum VariationLevel
{
    Stable,
    Increase,
    Decrease
}

/// <summary>
/// Résultat du chiffrage local d'une ligne. Les prix sont par unité de base de l'ingrédient (non arrondis). <see cref="Variation"/> est une
/// fraction ((nouveau − ancien) ÷ ancien), null sans ancien prix. Prix et variation ne sont renseignés que si <see cref="Issue"/> est
/// <see cref="ReceiptLineIssue.None"/> ; une ligne ignorée n'a ni prix ni problème. C'est le prix de la ligne seule : quand un même ingrédient
/// figure sur plusieurs lignes, le service applique leur moyenne pondérée (le récapitulatif de validation montre le prix retenu).
/// </summary>
public sealed record ReceiptLineEvaluation(
    decimal? PricePerBaseUnit,
    BaseUnit? BaseUnit,
    decimal? OldPricePerBaseUnit,
    decimal? Variation,
    ReceiptLineIssue Issue)
{
    public static ReceiptLineEvaluation Ignored { get; } = new(null, null, null, null, ReceiptLineIssue.None);
}

/// <summary>
/// Une ligne éditable du ticket. Ce que le lecteur y avait lu (taux de TVA, origine de l'ingrédient proposé, « TVA à vérifier ») est conservé tel quel
/// pour reproduire en direct ce que fera <c>ReceiptService.SaveDraftAsync</c> : l'origine de l'ingrédient disparaît dès que l'ingrédient n'est plus
/// celui qui était enregistré, et « TVA à vérifier » dès que le total change.
/// </summary>
public sealed class ReceiptReviewLine
{
    // Échelles des colonnes (voir ReceiptService) : le service arrondit ainsi avant de comparer et de chiffrer.
    private const int QuantityScale = 4;
    private const int AmountScale = 2;

    private int? _storedIngredientId;
    private MatchSource? _storedMatchSource;
    private bool _storedVatToCheck;
    private decimal? _storedTotal;

    /// <summary>Identifiant de la ligne du ticket ; null pour une ligne ajoutée à la main.</summary>
    public int? Id { get; private set; }

    public string RawLabel { get; set; } = "";

    public decimal? Quantity { get; set; }

    public MeasureUnit? Unit { get; set; }

    /// <summary>Total hors TVA de la ligne.</summary>
    public decimal? LineTotalExclVat { get; set; }

    /// <summary>Taux de TVA lu (fraction), non modifiable ici : renvoyé tel quel au service.</summary>
    public decimal? VatRate { get; private set; }

    public int? IngredientId { get; set; }

    public bool IsIgnored { get; set; }

    /// <summary>La TVA de la ligne reste à confirmer : ticket TTC au taux inconnu, total pas encore corrigé.</summary>
    public bool VatToCheck => _storedVatToCheck && Round(LineTotalExclVat, AmountScale) == _storedTotal;

    /// <summary>Origine de l'ingrédient proposé par la lecture ; null une fois l'ingrédient changé (choix manuel) ou pour une ligne ajoutée à la main.</summary>
    public MatchSource? MatchSource => IngredientId == _storedIngredientId ? _storedMatchSource : null;

    internal static ReceiptReviewLine From(ReceiptLine line) => new()
    {
        Id = line.Id,
        RawLabel = line.RawLabel,
        Quantity = line.Quantity,
        Unit = line.Unit,
        LineTotalExclVat = line.LineTotalExclVat,
        VatRate = line.VatRate,
        IngredientId = line.IngredientId,
        IsIgnored = line.IsIgnored,
        _storedIngredientId = line.IngredientId,
        _storedMatchSource = line.MatchSource,
        _storedVatToCheck = line.VatToCheck,
        _storedTotal = Round(line.LineTotalExclVat, AmountScale)
    };

    internal ReceiptLineEvaluation Evaluate(IReadOnlyDictionary<int, Ingredient> ingredients)
    {
        if (IsIgnored)
            return ReceiptLineEvaluation.Ignored;

        var ingredient = IngredientId is { } id ? ingredients.GetValueOrDefault(id) : null;
        var price = ReceiptLinePricing.Compute(Round(Quantity, QuantityScale), Unit, Round(LineTotalExclVat, AmountScale), ingredient);
        var oldPrice = ingredient?.CurrentPricePerBaseUnit;

        return new ReceiptLineEvaluation(
            price.PricePerBaseUnit,
            ingredient?.BaseUnit,
            oldPrice,
            ReceiptReviewForm.Variation(oldPrice, price.PricePerBaseUnit),
            price.Issue);
    }

    private static decimal? Round(decimal? value, int scale) =>
        value is { } number ? Math.Round(number, scale, MidpointRounding.AwayFromZero) : null;
}

/// <summary>
/// État de l'écran de validation d'un ticket, sans dépendance à l'interface : se construit depuis le ticket enregistré (<see cref="From"/>),
/// se chiffre en direct sans accès à la base (<see cref="Evaluate"/>, à partir des ingrédients chargés une fois) et se transforme en
/// <see cref="ReceiptDraftInput"/> pour l'enregistrement (<see cref="ToDraftInput"/>).
/// </summary>
public sealed class ReceiptReviewForm
{
    /// <summary>Au-delà de ± 5 %, la variation d'un prix est mise en évidence.</summary>
    public const decimal VariationThreshold = 0.05m;

    private ReceiptReviewForm(int receiptId) => ReceiptId = receiptId;

    public int ReceiptId { get; }

    public int? SupplierId { get; set; }

    /// <summary>Seul le jour compte.</summary>
    public DateTime? ReceiptDate { get; set; }

    public string? InvoiceNumber { get; set; }

    /// <summary>La version du ticket au chargement : renvoyée à l'enregistrement pour refuser d'écraser une modification faite entre-temps.</summary>
    public uint Version { get; private init; }

    public List<ReceiptReviewLine> Lines { get; } = [];

    /// <summary>Lignes qui seront appliquées (non ignorées).</summary>
    public int ActiveLineCount => Lines.Count(l => !l.IsIgnored);

    /// <summary>Lignes qui seront appliquées dont la TVA reste « à vérifier ».</summary>
    public int LinesWithVatToCheck => Lines.Count(l => !l.IsIgnored && l.VatToCheck);

    /// <summary>Formulaire du ticket enregistré ; les lignes gardent l'ordre reçu (le service les trie par position).</summary>
    public static ReceiptReviewForm From(Receipt receipt)
    {
        var form = new ReceiptReviewForm(receipt.Id)
        {
            SupplierId = receipt.SupplierId,
            ReceiptDate = receipt.ReceiptDate?.Date,
            InvoiceNumber = receipt.InvoiceNumber,
            Version = receipt.Version
        };
        form.Lines.AddRange(receipt.Lines.Select(ReceiptReviewLine.From));
        return form;
    }

    /// <summary>Le brouillon à enregistrer : les valeurs saisies telles quelles (le service arrondit), les lignes dans l'ordre affiché.</summary>
    public ReceiptDraftInput ToDraftInput() => new(
        ReceiptId,
        SupplierId,
        ReceiptDate,
        InvoiceNumber,
        Lines.Select(l => new ReceiptLineInput(l.Id, l.RawLabel, l.Quantity, l.Unit, l.LineTotalExclVat, l.VatRate, l.IngredientId, l.IsIgnored)).ToList(),
        Version);

    /// <summary>Chiffre chaque ligne telle qu'elle est saisie, sans accès à la base : une entrée par ligne, dans le même ordre.</summary>
    public IReadOnlyList<ReceiptLineEvaluation> Evaluate(IReadOnlyDictionary<int, Ingredient> ingredients) =>
        Lines.Select(l => l.Evaluate(ingredients)).ToList();

    /// <summary>Lignes avec un problème bloquant : le service refuserait d'appliquer le ticket.</summary>
    public static int BlockingIssueCount(IReadOnlyList<ReceiptLineEvaluation> evaluation) =>
        evaluation.Count(e => e.Issue != ReceiptLineIssue.None);

    /// <summary>(nouveau − ancien) ÷ ancien, sans arrondi ; null sans ancien prix positif ou sans nouveau prix.</summary>
    public static decimal? Variation(decimal? oldPrice, decimal? newPrice) =>
        oldPrice is > 0m && newPrice is { } price ? (price - oldPrice.Value) / oldPrice.Value : null;

    /// <summary>
    /// Titre du récapitulatif d'un ticket appliqué. Si aucun prix courant n'a été remplacé (ticket plus ancien que l'historique de chaque ingrédient),
    /// seul l'historique a été enrichi : on ne dit pas que des prix ont été mis à jour. Dès qu'un prix courant a changé, le titre reste « Prix mis à jour ».
    /// </summary>
    public static string ApplyHeadline(ApplyResult result)
    {
        var count = result.Prices.Count;
        var subject = count == 1 ? "1 ingrédient" : $"{count} ingrédients";

        return result.Prices.Count > 0 && result.Prices.All(p => !p.ReplacedCurrent)
            ? $"Historique enrichi pour {subject} (ticket plus ancien)"
            : $"Prix mis à jour pour {subject}";
    }

    /// <summary>Hausse au-delà de +5 %, baisse en dessous de −5 % ; 5 % pile reste stable.</summary>
    public static VariationLevel LevelOf(decimal variation) =>
        variation > VariationThreshold ? VariationLevel.Increase
        : variation < -VariationThreshold ? VariationLevel.Decrease
        : VariationLevel.Stable;
}
