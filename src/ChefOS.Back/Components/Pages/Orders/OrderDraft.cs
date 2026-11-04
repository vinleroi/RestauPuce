using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Orders;

/// <summary>Une ligne de la commande en cours de saisie. La quantité est vide tant que le champ l'est : le service refuse alors la ligne.</summary>
public sealed class OrderLineDraft
{
    internal OrderLineDraft(int recipeId, string dishName, int quantity, string? note, decimal? unitPriceInclVat)
    {
        RecipeId = recipeId;
        DishName = dishName;
        Quantity = quantity;
        Note = note;
        UnitPriceInclVat = unitPriceInclVat;
    }

    public int RecipeId { get; }

    public string DishName { get; }

    public int? Quantity { get; set; }

    /// <summary>Remarque de cuisine (« sans oignon »).</summary>
    public string? Note { get; set; }

    /// <summary>Prix de vente TTC actuel du plat quand la ligne vient d'être ajoutée ; null pour une ligne d'une commande existante (son prix figé n'est pas connu ici).</summary>
    public decimal? UnitPriceInclVat { get; }
}

/// <summary>
/// La commande en cours de saisie ou de modification, sans dépendance à l'interface : source, table ou client, note et lignes.
/// Une commande existante se modifie par ses lignes seulement (le service ne change ni la source, ni la table, ni la note).
/// </summary>
public sealed class OrderDraft
{
    private readonly List<OrderLineDraft> _lines = [];

    private OrderDraft()
    {
    }

    public OrderSource Source { get; set; } = OrderSource.Salle;

    public string? TableOrCustomer { get; set; }

    public string? Note { get; set; }

    public IReadOnlyList<OrderLineDraft> Lines => _lines;

    public static OrderDraft New() => new();

    public static OrderDraft From(OrderSummary order)
    {
        var draft = new OrderDraft { Source = order.Source, TableOrCustomer = order.TableOrCustomer };
        foreach (var line in order.Lines)
            draft._lines.Add(new OrderLineDraft(line.RecipeId, line.DishName, line.Quantity, line.Note, unitPriceInclVat: null));

        return draft;
    }

    /// <summary>Ajoute une portion du plat : à la ligne du plat s'il y en a déjà une, sinon sur une nouvelle ligne.</summary>
    public void AddDish(int recipeId, string dishName, decimal? unitPriceInclVat)
    {
        var existing = _lines.FirstOrDefault(l => l.RecipeId == recipeId);
        if (existing is null)
            _lines.Add(new OrderLineDraft(recipeId, dishName, 1, null, unitPriceInclVat));
        else
            existing.Quantity = (existing.Quantity ?? 0) + 1;
    }

    public void Remove(OrderLineDraft line) => _lines.Remove(line);

    /// <summary>
    /// Total TTC aux prix actuels des plats, tant que la commande est nouvelle ; null sans ligne, ou dès qu'une ligne reprend une commande existante
    /// (ses prix figés ne sont pas connus ici).
    /// </summary>
    public decimal? EstimatedTotal =>
        _lines.Count == 0 || _lines.Any(l => l.UnitPriceInclVat is null)
            ? null
            : _lines.Sum(l => (l.Quantity ?? 0) * l.UnitPriceInclVat!.Value);

    public OrderInput ToInput() => new(Source, TableOrCustomer, Note, ToLineInputs());

    public IReadOnlyList<OrderLineInput> ToLineInputs() =>
        _lines.Select(l => new OrderLineInput(l.RecipeId, l.Quantity ?? 0, l.Note)).ToList();

    /// <summary>Une commande se modifie et s'annule tant qu'elle n'est ni servie ni annulée.</summary>
    public static bool IsOpen(OrderStatus status) => status is OrderStatus.Nouvelle or OrderStatus.EnPreparation or OrderStatus.Prete;
}
