using ChefOS.Core.Common;
using ChefOS.Core.Recipes;
using ChefOS.Core.Sales;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Commandes prises dans ChefOS (salle, à emporter, téléphone, vitrine) : numéro séquentiel par restaurant, cycle de statut
/// <c>Nouvelle → EnPreparation → Prete → Servie</c> (ou <c>Annulee</c> tant que la commande n'est pas servie), prix figés à la prise de commande.
/// Les clés étrangères de la base sont à colonne unique : la vérification qu'un plat ou une commande appartient bien au restaurant courant
/// se fait ici, via le contexte filtré par restaurant (qui estampille aussi les ajouts).
/// Le jour d'une commande est le jour du calendrier UTC de sa création.
/// </summary>
public class OrderService(IDbContextFactory<ChefOsDbContext> factory)
{
    private const string NoLineMessage = "Ajoutez au moins un plat";
    private const string QuantityMessage = "La quantité doit être d'au moins 1";
    private const string QuantityTooHighMessage = "Quantité trop élevée";
    private const string UnknownDishMessage = "Plat introuvable";
    private const string UnknownOrderMessage = "Commande introuvable";
    private const string StatusChangedMessage = "La commande a déjà changé de statut";
    private const string NumberMessage = "Impossible d'attribuer un numéro de commande, réessayez";
    private const string ServedCannotBeEditedMessage = "Une commande servie ne peut plus être modifiée";
    private const string CancelledCannotBeEditedMessage = "Une commande annulée ne peut plus être modifiée";
    private const string AlreadyServedMessage = "Cette commande est déjà servie";
    private const string CancelledCannotAdvanceMessage = "Une commande annulée ne peut plus avancer";
    private const string ServedCannotBeCancelledMessage = "Une commande servie ne peut pas être annulée";

    private const string OrderNumberIndex = "IX_Orders_Number";

    /// <summary>Plafond de portions par ligne de commande : au-delà, c'est une faute de frappe.</summary>
    private const int MaxLineQuantity = 1000;

    /// <summary>Nouveaux essais de numérotation après une collision sur <c>IX_Orders_Number</c> (deux commandes créées au même instant).</summary>
    private const int NumberRetries = 5;

    /// <summary>
    /// Crée une commande « Nouvelle » avec ses lignes, aux prix et TVA actuels des plats (figés). Le numéro est le plus grand numéro du
    /// restaurant plus un, calculé et inséré dans une transaction ; si une commande concurrente prend le même numéro (violation de l'index
    /// unique), l'opération est reprise avec un nouveau contexte, jusqu'à 5 nouveaux essais.
    /// </summary>
    /// <returns>L'identifiant de la commande créée.</returns>
    /// <exception cref="DomainValidationException">
    /// Aucune ligne, quantité inférieure à 1 ou supérieure à 1 000, plat inconnu (ou archivé, ou qui n'est pas un plat du restaurant courant), plat sans prix de vente,
    /// ou numéro impossible à attribuer.
    /// </exception>
    public async Task<int> CreateAsync(OrderInput input)
    {
        RequireLines(input.Lines);
        var lines = await PriceLinesAsync(input.Lines, existing: null);

        for (var attempt = 0; attempt <= NumberRetries; attempt++)
        {
            // Nouveau contexte à chaque essai : celui d'une écriture refusée garde des entités dans un état incohérent.
            await using var db = await factory.CreateDbContextAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();

            var now = DateTime.UtcNow;
            var number = (await db.Orders.MaxAsync(o => (int?)o.Number) ?? 0) + 1;
            var order = new Order
            {
                Number = number,
                CreatedAt = now,
                Source = input.Source,
                Status = OrderStatus.Nouvelle,
                StatusChangedAt = now,
                TableOrCustomer = NullIfBlank(input.TableOrCustomer),
                Note = NullIfBlank(input.Note),
                Lines = lines.Select(l => l.ToEntity()).ToList()
            };
            db.Orders.Add(order);

            try
            {
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
                return order.Id;
            }
            catch (DbUpdateException ex) when (IsOrderNumberConflict(ex))
            {
                // Annulée avec la transaction (libérée en quittant la portée) ; le numéro est recalculé à l'essai suivant.
            }
        }

        throw new DomainValidationException(NumberMessage);
    }

    /// <summary>
    /// Remplace les lignes d'une commande qui n'est ni servie ni annulée. Une ligne qui reprend un plat déjà présent dans la commande garde ses
    /// prix figés ; un plat ajouté prend le prix actuel (et doit être un plat non archivé, avec un prix de vente).
    /// </summary>
    /// <exception cref="DomainValidationException">Commande inconnue, servie ou annulée ; lignes invalides (voir <see cref="CreateAsync"/>).</exception>
    public async Task UpdateLinesAsync(int orderId, IReadOnlyList<OrderLineInput> lines)
    {
        await using var db = await factory.CreateDbContextAsync();

        var order = await RequireOrderAsync(db, orderId, includeLines: true);
        if (order.Status == OrderStatus.Servie)
            throw new DomainValidationException(ServedCannotBeEditedMessage);
        if (order.Status == OrderStatus.Annulee)
            throw new DomainValidationException(CancelledCannotBeEditedMessage);

        RequireLines(lines);
        var priced = await PriceLinesAsync(lines, order.Lines);

        // Les anciennes lignes sont supprimées par orphelinage (clé étrangère obligatoire) ; les nouvelles sont insérées dans le même SaveChanges.
        order.Lines.Clear();
        order.Lines.AddRange(priced.Select(l => l.ToEntity()));

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Fait passer la commande au statut suivant (<c>Nouvelle → EnPreparation → Prete → Servie</c>), à condition qu'elle soit encore à <paramref name="expectedFrom"/> :
    /// l'appelant dit de quel statut il part (celui qu'il affiche), pour qu'un écran périmé ne fasse pas faire une autre étape que celle de son bouton
    /// (« Marquer prête » ne doit jamais servir la commande). Le changement est une mise à jour conditionnelle (« uniquement si le statut est encore celui qu'on a lu ») :
    /// deux clics simultanés ne peuvent pas sauter une étape, le second est refusé.
    /// </summary>
    /// <param name="expectedFrom">Le statut que l'appelant croit être celui de la commande.</param>
    /// <exception cref="DomainValidationException">
    /// Commande inconnue ; statut différent de <paramref name="expectedFrom"/> (« La commande a déjà changé de statut ») ou changé entre-temps par un autre appel ;
    /// commande annulée ou déjà servie.
    /// </exception>
    public async Task AdvanceAsync(int orderId, OrderStatus expectedFrom)
    {
        await using var db = await factory.CreateDbContextAsync();

        var current = (await RequireOrderAsync(db, orderId, includeLines: false)).Status;
        if (current != expectedFrom)
            throw new DomainValidationException(StatusChangedMessage);

        var next = current switch
        {
            OrderStatus.Nouvelle => OrderStatus.EnPreparation,
            OrderStatus.EnPreparation => OrderStatus.Prete,
            OrderStatus.Prete => OrderStatus.Servie,
            OrderStatus.Servie => throw new DomainValidationException(AlreadyServedMessage),
            _ => throw new DomainValidationException(CancelledCannotAdvanceMessage)
        };

        await ChangeStatusAsync(db, orderId, current, next);
    }

    /// <summary>
    /// Annule la commande, tant qu'elle n'est pas servie et qu'elle est encore à <paramref name="expectedFrom"/> (voir <see cref="AdvanceAsync"/>).
    /// Annuler une commande que l'appelant sait déjà annulée (<paramref name="expectedFrom"/> = <c>Annulee</c>) ne change rien.
    /// </summary>
    /// <exception cref="DomainValidationException">Commande inconnue ou servie ; statut différent de <paramref name="expectedFrom"/> ou changé entre-temps par un autre appel.</exception>
    public async Task CancelAsync(int orderId, OrderStatus expectedFrom)
    {
        await using var db = await factory.CreateDbContextAsync();

        var current = (await RequireOrderAsync(db, orderId, includeLines: false)).Status;
        if (current != expectedFrom)
            throw new DomainValidationException(StatusChangedMessage);

        if (current == OrderStatus.Servie)
            throw new DomainValidationException(ServedCannotBeCancelledMessage);
        if (current == OrderStatus.Annulee)
            return;

        await ChangeStatusAsync(db, orderId, current, OrderStatus.Annulee);
    }

    /// <summary>
    /// Les commandes créées le jour UTC de <paramref name="dayUtc"/> (de 00:00 inclus à 24:00 exclu), annulées comprises, triées par numéro.
    /// Le total est la somme des quantités × prix figés des lignes, non arrondie.
    /// </summary>
    public async Task<IReadOnlyList<OrderSummary>> ListForDayAsync(DateTime dayUtc)
    {
        var start = UtcDays.Of(dayUtc);
        var end = start.AddDays(1);

        await using var db = await factory.CreateDbContextAsync();

        var orders = await db.Orders.AsNoTracking()
            .Include(o => o.Lines.OrderBy(l => l.Id))
            .Where(o => o.CreatedAt >= start && o.CreatedAt < end)
            .OrderBy(o => o.Number)
            .ToListAsync();

        var recipeIds = orders.SelectMany(o => o.Lines).Select(l => l.RecipeId).Distinct().ToList();
        var names = await db.Recipes.AsNoTracking()
            .Where(r => recipeIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name);

        return orders
            .Select(o => new OrderSummary(
                o.Id,
                o.Number,
                DateTime.SpecifyKind(o.CreatedAt, DateTimeKind.Utc),
                o.Source,
                o.Status,
                o.TableOrCustomer,
                o.Lines.Sum(l => l.Quantity * l.UnitPriceInclVat),
                o.Lines.Select(l => new OrderLineSummary(l.RecipeId, names[l.RecipeId], l.Quantity, l.Note)).ToList()))
            .ToList();
    }

    /// <summary>
    /// Passe le statut de <paramref name="from"/> à <paramref name="to"/> en une instruction, uniquement si la commande est encore à <paramref name="from"/> :
    /// si un autre appel l'a déjà changé, aucune ligne n'est modifiée et le changement est refusé.
    /// </summary>
    private static async Task ChangeStatusAsync(ChefOsDbContext db, int orderId, OrderStatus from, OrderStatus to)
    {
        var changedAt = DateTime.UtcNow;
        var changed = await db.Orders
            .Where(o => o.Id == orderId && o.Status == from)
            .ExecuteUpdateAsync(set => set
                .SetProperty(o => o.Status, to)
                .SetProperty(o => o.StatusChangedAt, changedAt));
        if (changed != 1)
            throw new DomainValidationException(StatusChangedMessage);
    }

    private static async Task<Order> RequireOrderAsync(ChefOsDbContext db, int id, bool includeLines)
    {
        var query = db.Orders.AsQueryable();
        if (includeLines)
            query = query.Include(o => o.Lines);

        return await query.FirstOrDefaultAsync(o => o.Id == id)
               ?? throw new DomainValidationException(UnknownOrderMessage);
    }

    private static void RequireLines(IReadOnlyList<OrderLineInput> lines)
    {
        if (lines.Count == 0)
            throw new DomainValidationException(NoLineMessage);
        if (lines.Any(l => l.Quantity < 1))
            throw new DomainValidationException(QuantityMessage);
        if (lines.Any(l => l.Quantity > MaxLineQuantity))
            throw new DomainValidationException(QuantityTooHighMessage);
    }

    /// <summary>
    /// Associe chaque ligne saisie à son plat et à ses prix figés. Pour une modification, une ligne qui reprend un plat déjà présent dans
    /// <paramref name="existing"/> reprend les prix de la ligne d'origine (appariées dans l'ordre) ; sinon le plat doit être un plat non archivé,
    /// du restaurant courant, avec un prix de vente et une TVA, dont les valeurs actuelles sont figées.
    /// </summary>
    private async Task<List<PricedLine>> PriceLinesAsync(IReadOnlyList<OrderLineInput> lines, IReadOnlyList<OrderLine>? existing)
    {
        var keep = (existing ?? [])
            .GroupBy(l => l.RecipeId)
            .ToDictionary(g => g.Key, g => new Queue<OrderLine>(g.OrderBy(l => l.Id)));

        var priced = new List<PricedLine>();
        var newDishIds = new HashSet<int>();
        foreach (var line in lines)
        {
            if (keep.TryGetValue(line.RecipeId, out var queue) && queue.Count > 0)
            {
                var original = queue.Dequeue();
                priced.Add(new PricedLine(line.RecipeId, line.Quantity, original.UnitPriceInclVat, original.VatRate, NullIfBlank(line.Note)));
            }
            else
            {
                newDishIds.Add(line.RecipeId);
                priced.Add(new PricedLine(line.RecipeId, line.Quantity, null, null, NullIfBlank(line.Note)));
            }
        }

        if (newDishIds.Count == 0)
            return priced;

        await using var db = await factory.CreateDbContextAsync();
        var dishes = await db.Recipes.AsNoTracking()
            .Where(r => r.Kind == RecipeKind.Dish && !r.IsArchived && newDishIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id);

        for (var i = 0; i < priced.Count; i++)
        {
            if (priced[i].UnitPriceInclVat is not null)
                continue;

            var dish = dishes.GetValueOrDefault(priced[i].RecipeId)
                       ?? throw new DomainValidationException(UnknownDishMessage);
            if (dish.SellingPriceInclVat is null || dish.VatRate is null)
                throw new DomainValidationException($"Le plat {dish.Name} n'a pas de prix de vente");

            priced[i] = priced[i] with { UnitPriceInclVat = dish.SellingPriceInclVat, VatRate = dish.VatRate };
        }

        return priced;
    }

    private sealed record PricedLine(int RecipeId, int Quantity, decimal? UnitPriceInclVat, decimal? VatRate, string? Note)
    {
        public OrderLine ToEntity() => new()
        {
            RecipeId = RecipeId,
            Quantity = Quantity,
            UnitPriceInclVat = UnitPriceInclVat!.Value,
            VatRate = VatRate!.Value,
            Note = Note
        };
    }

    private static bool IsOrderNumberConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OrderNumberIndex };

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
