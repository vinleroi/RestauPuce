using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Sales;

/// <summary>
/// Une case de la grille : la quantité d'un plat pour un jour et un service, ou son total de semaine. Elle se souvient de la quantité enregistrée
/// pour savoir si le chef l'a modifiée. Une case vide vaut 0 : vider une case enregistrée envoie 0, ce qui supprime la saisie.
/// </summary>
public sealed class SalesGridCell
{
    // Échelle de la colonne (voir SalesService) : le service arrondit ainsi avant d'enregistrer, une différence plus fine n'est pas une modification.
    private const int QuantityScale = 4;

    private readonly decimal _stored;

    internal SalesGridCell(int recipeId, DateTime date, ServicePeriod service, bool isWeeklyTotal, decimal stored)
    {
        RecipeId = recipeId;
        Date = date;
        Service = service;
        IsWeeklyTotal = isWeeklyTotal;
        _stored = Round(stored);
        Quantity = stored == 0m ? null : stored;
    }

    public int RecipeId { get; }

    /// <summary>Jour de la case ; le lundi pour un total de semaine.</summary>
    public DateTime Date { get; }

    public ServicePeriod Service { get; }

    public bool IsWeeklyTotal { get; }

    /// <summary>Quantité saisie (portions, demi-portion permise) ; null quand la case est vide.</summary>
    public decimal? Quantity { get; set; }

    /// <summary>La quantité (vide = 0) n'est plus celle qui est enregistrée.</summary>
    public bool IsModified => Round(Quantity ?? 0m) != _stored;

    internal SalesCell ToSalesCell() => new(RecipeId, Date, Service, IsWeeklyTotal, Quantity ?? 0m);

    private static decimal Round(decimal value) => Math.Round(value, QuantityScale, MidpointRounding.AwayFromZero);
}

/// <summary>Un plat de la grille : sept jours × trois services, plus le total de semaine saisi d'un bloc.</summary>
public sealed class SalesGridRow
{
    private static readonly ServicePeriod[] Services = Enum.GetValues<ServicePeriod>();

    private readonly Dictionary<(int Day, ServicePeriod Service), SalesGridCell> _cells = [];

    internal SalesGridRow(int recipeId, string name, decimal? unitPriceInclVat, decimal? vatRate, DateTime monday,
        IReadOnlyDictionary<(int Day, ServicePeriod Service), decimal> stored, decimal storedWeeklyTotal)
    {
        RecipeId = recipeId;
        Name = name;
        UnitPriceInclVat = unitPriceInclVat;
        VatRate = vatRate;

        for (var day = 0; day < SalesGridForm.DayCount; day++)
        {
            foreach (var service in Services)
                _cells[(day, service)] = new SalesGridCell(recipeId, monday.AddDays(day), service, false, stored.GetValueOrDefault((day, service)));
        }

        // Un total de semaine est daté du lundi et n'a pas de service : il est enregistré en « Journée ».
        WeeklyTotal = new SalesGridCell(recipeId, monday, ServicePeriod.Journee, true, storedWeeklyTotal);
    }

    public int RecipeId { get; }

    public string Name { get; }

    /// <summary>Prix de vente TTC actuel du plat.</summary>
    public decimal? UnitPriceInclVat { get; }

    /// <summary>Taux de TVA actuel du plat (fraction).</summary>
    public decimal? VatRate { get; }

    public SalesGridCell WeeklyTotal { get; }

    /// <param name="dayIndex">0 pour le lundi, 6 pour le dimanche.</param>
    public SalesGridCell Cell(int dayIndex, ServicePeriod service) => _cells[(dayIndex, service)];

    /// <summary>Toutes les cases, jour après jour puis le total de semaine en dernier.</summary>
    internal IEnumerable<SalesGridCell> Cells()
    {
        for (var day = 0; day < SalesGridForm.DayCount; day++)
        {
            foreach (var service in Services)
                yield return _cells[(day, service)];
        }

        yield return WeeklyTotal;
    }

    /// <summary>Les trois services d'un jour additionnés.</summary>
    public decimal DayTotal(int dayIndex) => Services.Sum(service => Cell(dayIndex, service).Quantity ?? 0m);

    /// <summary>Les sept jours d'un service additionnés (sans le total de semaine).</summary>
    public decimal ServiceTotal(ServicePeriod service) =>
        Enumerable.Range(0, SalesGridForm.DayCount).Sum(day => Cell(day, service).Quantity ?? 0m);

    /// <summary>Toutes les portions de la semaine : chaque jour et chaque service, plus le total de semaine.</summary>
    public decimal Total => Cells().Sum(cell => cell.Quantity ?? 0m);

    /// <summary>
    /// Chiffre d'affaires HT estimé : portions × prix TTC actuel ÷ (1 + TVA), non arrondi. C'est une estimation : une vente déjà enregistrée garde le prix
    /// de sa saisie, qui peut différer du prix actuel. Null sans prix ou sans taux de TVA.
    /// </summary>
    public decimal? RevenueExclVat =>
        UnitPriceInclVat is { } price && VatRate is { } vat ? Total * price / (1m + vat) : null;
}

/// <summary>
/// État de la grille des ventes d'une semaine, sans dépendance à l'interface : se construit depuis la semaine enregistrée (<see cref="From"/>),
/// suit les cases modifiées (<see cref="ModifiedCells"/>), calcule les totaux et le chiffre d'affaires en direct, sans accès à la base.
/// </summary>
public sealed class SalesGridForm
{
    public const int DayCount = 7;

    private SalesGridForm(DateTime monday, IReadOnlyList<SalesGridRow> rows)
    {
        Monday = monday;
        Rows = rows;
    }

    /// <summary>Le lundi de la semaine, à 00:00 UTC.</summary>
    public DateTime Monday { get; }

    public IReadOnlyList<SalesGridRow> Rows { get; }

    /// <param name="vatRates">Taux de TVA (fractions) des plats, par identifiant : la semaine ne porte que les prix.</param>
    public static SalesGridForm From(SalesWeek week, IReadOnlyDictionary<int, decimal> vatRates)
    {
        var monday = DateTime.SpecifyKind(week.Monday.Date, DateTimeKind.Utc);

        var rows = week.Dishes
            .Select(dish =>
            {
                var stored = new Dictionary<(int Day, ServicePeriod Service), decimal>();
                var weeklyTotal = 0m;
                foreach (var cell in dish.Cells)
                {
                    if (cell.IsWeeklyTotal)
                    {
                        weeklyTotal = cell.Quantity;
                        continue;
                    }

                    var day = (cell.Date.Date - monday).Days;
                    if (day is >= 0 and < DayCount)
                        stored[(day, cell.Service)] = cell.Quantity;
                }

                return new SalesGridRow(
                    dish.RecipeId, dish.Name, dish.UnitPriceInclVat, vatRates.TryGetValue(dish.RecipeId, out var vat) ? vat : null,
                    monday, stored, weeklyTotal);
            })
            .ToList();

        return new SalesGridForm(monday, rows);
    }

    public DateTime DateOf(int dayIndex) => Monday.AddDays(dayIndex);

    /// <summary>Une quantité midi ou soir est saisie : la grille doit montrer le détail, sinon les totaux compteraient des ventes invisibles.</summary>
    public bool HasServiceDetail =>
        Rows.Any(row => row.Cells().Any(cell => !cell.IsWeeklyTotal && cell.Service != ServicePeriod.Journee && (cell.Quantity ?? 0m) > 0m));

    /// <summary>Total d'un jour, tous plats et tous services.</summary>
    public decimal DayTotal(int dayIndex) => Rows.Sum(row => row.DayTotal(dayIndex));

    /// <summary>Somme de la colonne « Total semaine » (les totaux saisis d'un bloc).</summary>
    public decimal WeeklyTotalsSum => Rows.Sum(row => row.WeeklyTotal.Quantity ?? 0m);

    /// <summary>Toutes les portions de la semaine.</summary>
    public decimal Total => Rows.Sum(row => row.Total);

    /// <summary>Chiffre d'affaires HT estimé de la semaine (voir <see cref="SalesGridRow.RevenueExclVat"/>) ; un plat sans prix n'y compte pas.</summary>
    public decimal RevenueExclVat => Rows.Sum(row => row.RevenueExclVat ?? 0m);

    /// <summary>Les cases modifiées depuis l'enregistrement, dans l'ordre de la grille : seules celles-ci sont envoyées au service.</summary>
    public IReadOnlyList<SalesCell> ModifiedCells() =>
        Rows.SelectMany(row => row.Cells()).Where(cell => cell.IsModified).Select(cell => cell.ToSalesCell()).ToList();

    public int ModifiedCount => Rows.Sum(row => row.Cells().Count(cell => cell.IsModified));

    public bool IsDirty => Rows.Any(row => row.Cells().Any(cell => cell.IsModified));
}
