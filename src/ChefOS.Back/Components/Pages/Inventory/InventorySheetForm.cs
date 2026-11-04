using ChefOS.Back.Components.Shared;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Inventory;

/// <summary>
/// Une ligne de la feuille de comptage : un ingrédient, la quantité comptée et son unité. Sans quantité, l'ingrédient n'est pas compté
/// (zéro est un vrai comptage : « plus rien en stock »). L'unité d'office est celle qu'on pèse ou qu'on verse (kg, l, pièce).
/// </summary>
public sealed class InventoryLine
{
    // Échelle de la colonne (voir InventoryService) : le service arrondit ainsi avant d'enregistrer, une différence plus fine n'est pas une modification.
    private const int QuantityScale = 4;

    private readonly decimal? _storedQuantity;
    private readonly MeasureUnit _storedUnit;

    internal InventoryLine(StockCountSheetLine line)
    {
        IngredientId = line.IngredientId;
        Name = line.Name;
        Category = line.Category;
        BaseUnit = line.BaseUnit;
        AveragePieceWeightGrams = line.AveragePieceWeightGrams;

        _storedQuantity = line.Quantity is { } quantity ? Round(quantity) : null;
        _storedUnit = line.Unit ?? Ui.DefaultMeasureUnit(line.BaseUnit);
        Quantity = line.Quantity;
        Unit = _storedUnit;
    }

    public int IngredientId { get; }

    public string Name { get; }

    public string Category { get; }

    public BaseUnit BaseUnit { get; }

    /// <summary>Poids moyen d'une pièce de l'ingrédient : renseigné, il permet de le compter en pièces (ou en grammes s'il est à la pièce).</summary>
    public decimal? AveragePieceWeightGrams { get; }

    public decimal? Quantity { get; set; }

    public MeasureUnit Unit { get; set; }

    public bool IsCounted => Quantity is not null;

    /// <summary>
    /// Les unités proposées : celles qui se convertissent dans l'unité de base (les pièces pour un ingrédient en grammes dont le poids moyen est renseigné),
    /// l'unité d'office en premier. Une unité déjà choisie ou enregistrée hors de cette liste (le poids moyen a pu être retiré depuis) y reste,
    /// pour que la feuille puisse l'afficher.
    /// </summary>
    public IReadOnlyList<MeasureUnit> UnitChoices
    {
        get
        {
            var choices = Ui.ConvertibleUnits(BaseUnit, AveragePieceWeightGrams);
            return choices.Contains(Unit) ? choices : [.. choices, Unit];
        }
    }

    /// <summary>Ce qui est saisi n'est plus ce qui est enregistré : une quantité ajoutée, changée ou effacée, ou une autre unité sur une ligne comptée.</summary>
    public bool IsModified =>
        (Quantity is { } quantity ? Round(quantity) : (decimal?)null) != _storedQuantity
        || (Quantity is not null && Unit != _storedUnit);

    internal StockCountLineInput ToInput() => new(IngredientId, Quantity!.Value, Unit);

    private static decimal Round(decimal value) => Math.Round(value, QuantityScale, MidpointRounding.AwayFromZero);
}

/// <summary>Les lignes d'une catégorie d'ingrédients.</summary>
public sealed record InventoryGroup(string Category, IReadOnlyList<InventoryLine> Lines);

/// <summary>
/// État de la feuille de comptage d'un inventaire, sans dépendance à l'interface : se construit depuis la feuille enregistrée (<see cref="From"/>)
/// et se transforme en lignes à enregistrer (<see cref="ToInputs"/>). <c>InventoryService.SaveLinesAsync</c> prend l'ensemble complet de ce qui est
/// compté (un ingrédient absent n'est plus compté) : <see cref="ToInputs"/> renvoie donc toujours toutes les lignes qui ont une quantité,
/// modifiées ou non.
/// </summary>
public sealed class InventorySheetForm
{
    private InventorySheetForm(StockCountSheet sheet)
    {
        Id = sheet.Id;
        Date = sheet.Date;
        Note = sheet.Note;
        IsClosed = sheet.Status == StockCountStatus.Cloture;
        Lines = sheet.Lines.Select(line => new InventoryLine(line)).ToList();
    }

    public int Id { get; }

    /// <summary>Jour de l'inventaire (un jour de calendrier, en UTC).</summary>
    public DateTime Date { get; }

    public string? Note { get; }

    /// <summary>Un inventaire clôturé est en lecture seule.</summary>
    public bool IsClosed { get; }

    /// <summary>Tous les ingrédients de la feuille, triés par catégorie puis par nom (l'ordre du service).</summary>
    public IReadOnlyList<InventoryLine> Lines { get; }

    public static InventorySheetForm From(StockCountSheet sheet) => new(sheet);

    public int TotalCount => Lines.Count;

    public int CountedCount => Lines.Count(line => line.IsCounted);

    public bool IsDirty => Lines.Any(line => line.IsModified);

    /// <summary>
    /// Les lignes regroupées par catégorie, dans l'ordre de la feuille. <paramref name="filter"/> garde les ingrédients dont le nom ou la catégorie contient
    /// le texte (sans tenir compte de la casse ni des accents) ; une catégorie sans ligne n'apparaît pas.
    /// </summary>
    /// <param name="countedOnly">Ne garde que les ingrédients comptés (la vue d'un inventaire clôturé).</param>
    public IReadOnlyList<InventoryGroup> Groups(string? filter = null, bool countedOnly = false) =>
        Lines
            .Where(line => (!countedOnly || line.IsCounted) && (Ui.ContainsText(line.Name, filter) || Ui.ContainsText(line.Category, filter)))
            .GroupBy(line => line.Category, StringComparer.InvariantCultureIgnoreCase)
            .Select(group => new InventoryGroup(group.First().Category, group.ToList()))
            .ToList();

    /// <summary>Toutes les lignes qui ont une quantité : l'ensemble complet attendu par <c>SaveLinesAsync</c>.</summary>
    public IReadOnlyList<StockCountLineInput> ToInputs() =>
        Lines.Where(line => line.IsCounted).Select(line => line.ToInput()).ToList();
}
