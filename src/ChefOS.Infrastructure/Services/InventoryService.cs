using ChefOS.Core.Common;
using ChefOS.Core.Sales;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Inventaires physiques : un brouillon se remplit et se corrige, puis se clôture ; un inventaire clôturé est en lecture seule et fait foi
/// pour les écarts. Les quantités saisies dans n'importe quelle unité compatible sont converties dans l'unité de base de l'ingrédient.
/// Les clés étrangères de la base sont à colonne unique : la vérification qu'un ingrédient ou un inventaire appartient bien au restaurant
/// courant se fait ici, via le contexte filtré par restaurant (qui estampille aussi les ajouts).
/// Le jour d'un inventaire est un jour du calendrier UTC.
/// </summary>
public class InventoryService(IDbContextFactory<ChefOsDbContext> factory)
{
    private const string UnknownCountMessage = "Inventaire introuvable";
    private const string ClosedMessage = "Inventaire clôturé";
    private const string ClosedCannotBeDeletedMessage = "Un inventaire clôturé ne peut pas être supprimé";
    private const string EmptyCloseMessage = "Saisissez au moins une quantité avant de clôturer";
    private const string FutureMessage = "La date ne peut pas être dans le futur";
    private const string NegativeQuantityMessage = "La quantité ne peut pas être négative";
    private const string QuantityTooHighMessage = "Quantité trop élevée";
    private const string UnknownIngredientMessage = "Ingrédient introuvable";

    /// <summary>Plafond d'une saisie, dans l'unité saisie : au-delà, c'est une faute de frappe.</summary>
    private const decimal MaxQuantity = 1_000_000m;

    // Échelle de la colonne numeric : arrondir avant de valider évite qu'une quantité négative minuscule soit stockée comme 0, ou l'inverse.
    private const int QuantityScale = 4;

    /// <summary>Crée un inventaire en brouillon, daté du jour UTC de <paramref name="dayUtc"/> (au plus demain, comme pour les tickets).</summary>
    /// <exception cref="DomainValidationException">Date postérieure à demain.</exception>
    public async Task<int> CreateAsync(DateTime dayUtc, string? note)
    {
        var day = UtcDays.Of(dayUtc);
        if (UtcDays.IsInTheFuture(day))
            throw new DomainValidationException(FutureMessage);

        await using var db = await factory.CreateDbContextAsync();

        var count = new StockCount
        {
            Date = day,
            Status = StockCountStatus.Brouillon,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()
        };
        db.StockCounts.Add(count);
        await db.SaveChangesAsync();

        return count.Id;
    }

    /// <summary>
    /// La feuille de comptage : les ingrédients non archivés et, parmi les archivés, ceux déjà comptés dans cet inventaire, triés par catégorie puis
    /// par nom, chacun avec la quantité et l'unité saisies (null tant qu'il n'est pas compté). Disponible aussi pour un inventaire clôturé.
    /// </summary>
    /// <exception cref="DomainValidationException">Inventaire inconnu.</exception>
    public async Task<StockCountSheet> GetSheetAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        var count = await db.StockCounts.AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id)
                    ?? throw new DomainValidationException(UnknownCountMessage);

        var linesByIngredient = count.Lines.ToDictionary(l => l.IngredientId);
        var countedIds = linesByIngredient.Keys.ToList();
        var ingredients = await db.Ingredients.AsNoTracking()
            .Where(i => !i.IsArchived || countedIds.Contains(i.Id))
            .ToListAsync();

        var rows = ingredients
            .OrderBy(i => i.Category, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.InvariantCultureIgnoreCase)
            .ThenBy(i => i.Id)
            .Select(i =>
            {
                linesByIngredient.TryGetValue(i.Id, out var line);
                return new StockCountSheetLine(i.Id, i.Name, i.Category, i.BaseUnit, line?.Quantity, line?.Unit);
            })
            .ToList();

        return new StockCountSheet(count.Id, DateTime.SpecifyKind(count.Date, DateTimeKind.Utc), count.Status, count.Note, rows);
    }

    /// <summary>
    /// Enregistre le comptage d'un brouillon, tout ou rien : toutes les lignes sont validées avant la moindre écriture, puis appliquées d'un seul
    /// <c>SaveChanges</c>. <paramref name="lines"/> est l'ensemble complet de ce qui est compté : un ingrédient qui n'y figure pas n'est plus compté
    /// (sa ligne est supprimée), une ligne existante est modifiée sur place, et si un ingrédient revient plusieurs fois la dernière valeur gagne.
    /// Une quantité de 0 est une vraie saisie (« plus rien en stock »). La quantité est arrondie à 4 décimales (échelle de la colonne) ;
    /// <c>BaseQuantity</c> est la quantité convertie dans l'unité de base de l'ingrédient.
    /// Les écritures prennent d'abord le verrou de la ligne de l'inventaire par une mise à jour conditionnelle (« encore en brouillon ») : une clôture
    /// qui se produit en même temps attend l'issue de la sauvegarde, et une sauvegarde qui arrive pendant une clôture est refusée une fois celle-ci validée.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// Inventaire inconnu ou clôturé, quantité négative ou supérieure à 1 000 000, ingrédient inconnu (ou d'un autre restaurant),
    /// unité incompatible avec l'unité de base de l'ingrédient.
    /// </exception>
    public async Task SaveLinesAsync(int id, IReadOnlyList<StockCountLineInput> lines)
    {
        await using var db = await factory.CreateDbContextAsync();

        var status = await db.StockCounts.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => (StockCountStatus?)c.Status)
            .FirstOrDefaultAsync();
        if (status is null)
            throw new DomainValidationException(UnknownCountMessage);
        if (status == StockCountStatus.Cloture)
            throw new DomainValidationException(ClosedMessage);

        var ingredientIds = lines.Select(l => l.IngredientId).Distinct().ToList();
        var ingredients = await db.Ingredients.AsNoTracking()
            .Where(i => ingredientIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id);

        // Toutes les lignes sont validées, y compris celles qu'une valeur plus tardive pour le même ingrédient écrasera.
        var counted = new Dictionary<int, CountedValue>();
        foreach (var line in lines)
        {
            var ingredient = ingredients.GetValueOrDefault(line.IngredientId)
                             ?? throw new DomainValidationException(UnknownIngredientMessage);

            var quantity = Math.Round(line.Quantity, QuantityScale, MidpointRounding.AwayFromZero);
            if (quantity < 0m)
                throw new DomainValidationException(NegativeQuantityMessage);
            if (quantity > MaxQuantity)
                throw new DomainValidationException(QuantityTooHighMessage);

            var baseQuantity = UnitConverter.ToBase(quantity, line.Unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams);
            if (!baseQuantity.Success)
                throw new DomainValidationException($"Unité incompatible pour {ingredient.Name}");

            counted[line.IngredientId] = new CountedValue(quantity, line.Unit, baseQuantity.Value);
        }

        await using var transaction = await db.Database.BeginTransactionAsync();

        // Verrou de l'inventaire : « encore en brouillon » en une instruction (qui ne change rien d'autre). Une clôture ou une suppression en cours
        // tient la ligne : cette instruction attend sa fin, puis ne trouve plus de brouillon et ne modifie aucune ligne : refus.
        var locked = await db.StockCounts
            .Where(c => c.Id == id && c.Status == StockCountStatus.Brouillon)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.Note, c => c.Note));
        if (locked != 1)
            throw new DomainValidationException(await RefusalAsync(db, id));

        var existing = (await db.StockCountLines.Where(l => l.StockCountId == id).ToListAsync())
            .ToDictionary(l => l.IngredientId);

        foreach (var (ingredientId, line) in existing)
        {
            if (!counted.ContainsKey(ingredientId))
                db.StockCountLines.Remove(line);
        }

        foreach (var (ingredientId, value) in counted)
        {
            if (!existing.TryGetValue(ingredientId, out var line))
            {
                line = new StockCountLine { StockCountId = id, IngredientId = ingredientId };
                db.StockCountLines.Add(line);
            }

            line.Quantity = value.Quantity;
            line.Unit = value.Unit;
            line.BaseQuantity = value.BaseQuantity;
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Clôture un brouillon : statut <see cref="StockCountStatus.Cloture"/> et <c>ClosedAt</c>. Une instruction conditionnelle (« encore en brouillon »,
    /// exactement une ligne modifiée) réserve l'inventaire : deux clôtures simultanées, ou une clôture et une sauvegarde, ne gagnent jamais ensemble.
    /// Une fois réservé, l'inventaire doit compter au moins une ligne (une quantité de 0 en est une) ; sinon la transaction est annulée et le brouillon reste intact.
    /// </summary>
    /// <exception cref="DomainValidationException">Inventaire inconnu, déjà clôturé, ou sans aucune ligne saisie.</exception>
    public async Task CloseAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var closedAt = DateTime.UtcNow;
        var claimed = await db.StockCounts
            .Where(c => c.Id == id && c.Status == StockCountStatus.Brouillon)
            .ExecuteUpdateAsync(set => set
                .SetProperty(c => c.Status, StockCountStatus.Cloture)
                .SetProperty(c => c.ClosedAt, (DateTime?)closedAt));
        if (claimed != 1)
            throw new DomainValidationException(await RefusalAsync(db, id));

        // L'inventaire est réservé : plus aucune sauvegarde ne peut en changer les lignes avant la fin de cette transaction.
        if (!await db.StockCountLines.AnyAsync(l => l.StockCountId == id))
            throw new DomainValidationException(EmptyCloseMessage);

        await transaction.CommitAsync();
    }

    /// <summary>Supprime un brouillon et ses lignes.</summary>
    /// <exception cref="DomainValidationException">Inventaire inconnu, ou clôturé (il fait foi pour les écarts).</exception>
    public async Task DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        // Les lignes partent par cascade en base.
        var deleted = await db.StockCounts
            .Where(c => c.Id == id && c.Status == StockCountStatus.Brouillon)
            .ExecuteDeleteAsync();
        if (deleted != 1)
            throw new DomainValidationException(
                await db.StockCounts.AnyAsync(c => c.Id == id) ? ClosedCannotBeDeletedMessage : UnknownCountMessage);
    }

    /// <summary>
    /// Les inventaires du restaurant courant, du plus récent au plus ancien (à date égale, le dernier créé d'abord).
    /// <see cref="StockCountSummary.Value"/> vaut Σ quantité de base × prix en vigueur à la date de l'inventaire (voir <see cref="PriceAsOf"/>), non arrondi.
    /// Les lignes dont l'ingrédient n'a aucun prix sont ignorées : la valeur reste calculée pour les ingrédients dont le prix est connu, donc elle
    /// peut sous-estimer un inventaire partiellement chiffré. Elle est null quand aucune ligne n'a de prix (inventaire vide, ou rien de chiffrable).
    /// </summary>
    public async Task<IReadOnlyList<StockCountSummary>> ListAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        var counts = await db.StockCounts.AsNoTracking()
            .OrderByDescending(c => c.Date)
            .ThenByDescending(c => c.Id)
            .ToListAsync();
        if (counts.Count == 0)
            return [];

        var lines = await db.StockCountLines.AsNoTracking()
            .Select(l => new { l.StockCountId, l.IngredientId, l.BaseQuantity })
            .ToListAsync();
        var ingredientIds = lines.Select(l => l.IngredientId).Distinct().ToList();

        var currentPrices = await db.Ingredients.AsNoTracking()
            .Where(i => ingredientIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.CurrentPricePerBaseUnit);

        // Un prix postérieur au dernier inventaire ne sert à rien (la liste est triée : le premier est le plus récent).
        var lastDayEnd = DateTime.SpecifyKind(counts[0].Date.Date.AddDays(1), DateTimeKind.Utc);
        var history = (await db.IngredientPrices.AsNoTracking()
                .Where(p => ingredientIds.Contains(p.IngredientId) && p.Date < lastDayEnd)
                .ToListAsync())
            .ToLookup(p => p.IngredientId);

        var linesByCount = lines.ToLookup(l => l.StockCountId);

        return counts
            .Select(c =>
            {
                var day = DateTime.SpecifyKind(c.Date, DateTimeKind.Utc);
                var countLines = linesByCount[c.Id].ToList();

                decimal? value = null;
                foreach (var line in countLines)
                {
                    var price = PriceAsOf.At(history[line.IngredientId], day, currentPrices.GetValueOrDefault(line.IngredientId));
                    if (price is { } known)
                        value = (value ?? 0m) + line.BaseQuantity * known;
                }

                return new StockCountSummary(c.Id, day, c.Status, countLines.Count, value);
            })
            .ToList();
    }

    /// <summary>Pourquoi une écriture conditionnelle sur un brouillon n'a rien modifié : l'inventaire est clôturé, ou n'existe pas (dans ce restaurant).</summary>
    private static async Task<string> RefusalAsync(ChefOsDbContext db, int id) =>
        await db.StockCounts.AnyAsync(c => c.Id == id) ? ClosedMessage : UnknownCountMessage;

    private readonly record struct CountedValue(decimal Quantity, MeasureUnit Unit, decimal BaseQuantity);
}
