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
/// Le jour d'une commande est le jour, dans le fuseau du restaurant, de sa création.
/// </summary>
public class OrderService(IDbContextFactory<ChefOsDbContext> factory, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

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
    private const string ModifiedElsewhereMessage = "La commande a été modifiée sur un autre appareil, rechargez";

    private const string OrderNumberIndex = "IX_Orders_Number";

    /// <summary>Les statuts d'une commande encore à traiter : elle reste visible au tableau tant qu'elle n'est ni servie ni annulée, même d'un jour précédent.</summary>
    private static readonly OrderStatus[] OpenStatuses = [OrderStatus.Nouvelle, OrderStatus.EnPreparation, OrderStatus.Prete];

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

            var order = AddNumberedOrder(db, input.Source, input.TableOrCustomer, input.Note, lines.Select(l => l.ToEntity()).ToList(), await NextNumberAsync(db), Now);

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
    /// Crée une commande « Nouvelle » à partir de lignes déjà figées (prix et TVA fournis), sans exiger que les plats soient encore actifs ni vendus :
    /// utilisé à l'arrivée en salle d'une réservation, dont la pré-commande garde les prix du jour de la réservation même si un plat a été archivé depuis.
    /// Les plats doivent tout de même appartenir au restaurant courant. Même numérotation que <see cref="CreateAsync"/>.
    /// Avec <paramref name="db"/>, l'insertion se fait dans le contexte (et la transaction) de l'appelant, sans nouvel essai : un conflit de numéro
    /// remonte en <see cref="DbUpdateException"/> (voir <see cref="IsOrderNumberConflict"/>) et l'appelant reprend toute sa transaction.
    /// </summary>
    internal async Task<int> CreateFromFrozenLinesAsync(OrderSource source, string? tableOrCustomer, string? note,
        IReadOnlyList<FrozenOrderLine> lines, ChefOsDbContext? db = null, DateTime? now = null)
    {
        if (lines.Count == 0)
            throw new DomainValidationException(NoLineMessage);
        if (lines.Any(l => l.Quantity < 1))
            throw new DomainValidationException(QuantityMessage);

        if (db is not null)
            return await InsertFrozenAsync(db, source, tableOrCustomer, note, lines, now);

        for (var attempt = 0; attempt <= NumberRetries; attempt++)
        {
            await using var own = await factory.CreateDbContextAsync();
            await using var transaction = await own.Database.BeginTransactionAsync();
            try
            {
                var id = await InsertFrozenAsync(own, source, tableOrCustomer, note, lines, now);
                await transaction.CommitAsync();
                return id;
            }
            catch (DbUpdateException ex) when (IsOrderNumberConflict(ex))
            {
            }
        }

        throw new DomainValidationException(NumberMessage);
    }

    private async Task<int> InsertFrozenAsync(ChefOsDbContext db, OrderSource source, string? table, string? note, IReadOnlyList<FrozenOrderLine> lines, DateTime? now)
    {
        var ids = lines.Select(l => l.RecipeId).Distinct().ToList();
        if (await db.Recipes.CountAsync(r => r.Kind == RecipeKind.Dish && ids.Contains(r.Id)) != ids.Count)
            throw new DomainValidationException(UnknownDishMessage);

        var entities = lines.Select(l => new OrderLine
        {
            RecipeId = l.RecipeId, Quantity = l.Quantity, UnitPriceInclVat = l.UnitPriceInclVat, VatRate = l.VatRate, Note = NullIfBlank(l.Note)
        }).ToList();
        var order = AddNumberedOrder(db, source, table, note, entities, await NextNumberAsync(db), now ?? Now);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private static async Task<int> NextNumberAsync(ChefOsDbContext db) => (await db.Orders.MaxAsync(o => (int?)o.Number) ?? 0) + 1;

    private Order AddNumberedOrder(ChefOsDbContext db, OrderSource source, string? table, string? note, List<OrderLine> lines, int number, DateTime now)
    {
        var order = new Order
        {
            Number = number,
            CreatedAt = now,
            Source = source,
            Status = OrderStatus.Nouvelle,
            StatusChangedAt = now,
            TableOrCustomer = NullIfBlank(table),
            Note = NullIfBlank(note),
            Lines = lines
        };
        db.Orders.Add(order);
        return order;
    }

    /// <summary>
    /// Remplace les lignes d'une commande qui n'est ni servie ni annulée. Une ligne qui reprend un plat déjà présent dans la commande garde ses
    /// prix figés ; un plat ajouté prend le prix actuel (et doit être un plat non archivé, avec un prix de vente).
    /// La commande est d'abord réservée par une mise à jour conditionnelle sans effet (« encore ni servie ni annulée »), dans une transaction : deux modifications
    /// simultanées passent l'une après l'autre (la seconde relit les lignes que la première a écrites) au lieu de se disputer les mêmes lignes, et une commande servie
    /// ou annulée entre-temps est refusée. Si les lignes ont quand même changé sous les pieds de l'appelant, il en est averti en français.
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// Commande inconnue, servie ou annulée ; lignes invalides (voir <see cref="CreateAsync"/>) ; commande modifiée sur un autre appareil pendant l'enregistrement.
    /// </exception>
    public async Task UpdateLinesAsync(int orderId, IReadOnlyList<OrderLineInput> lines)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();

        // Verrou de la commande : « ni servie ni annulée » en une instruction (qui ne change rien d'autre). Une modification concurrente tient la ligne :
        // cette instruction attend sa fin, puis réévalue la condition sur la commande telle qu'elle est devenue.
        var claimed = await db.Orders
            .Where(o => o.Id == orderId && o.Status != OrderStatus.Servie && o.Status != OrderStatus.Annulee)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.Note, o => o.Note));
        if (claimed != 1)
        {
            var status = await db.Orders.AsNoTracking()
                .Where(o => o.Id == orderId)
                .Select(o => (OrderStatus?)o.Status)
                .FirstOrDefaultAsync();
            throw new DomainValidationException(status switch
            {
                null => UnknownOrderMessage,
                OrderStatus.Servie => ServedCannotBeEditedMessage,
                _ => CancelledCannotBeEditedMessage
            });
        }

        RequireLines(lines);

        // Les lignes sont lues une fois la commande réservée : ce sont celles que la dernière modification validée a écrites.
        var order = await RequireOrderAsync(db, orderId, includeLines: true);
        var priced = await PriceLinesAsync(lines, order.Lines);

        // Les anciennes lignes sont supprimées par orphelinage (clé étrangère obligatoire) ; les nouvelles sont insérées dans le même SaveChanges.
        order.Lines.Clear();
        order.Lines.AddRange(priced.Select(l => l.ToEntity()));

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Une ligne à supprimer n'existe plus : la commande a été modifiée entre la lecture et l'enregistrement.
            throw new DomainValidationException(ModifiedElsewhereMessage);
        }

        await transaction.CommitAsync();
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
    /// Les commandes créées le jour (étiquette de jour, lue sans fuseau) <paramref name="day"/> du restaurant (de 00:00 locale incluse à 24:00 exclue), annulées comprises, triées par numéro, précédées des commandes
    /// encore à traiter (nouvelles, en préparation ou prêtes) créées un jour plus tôt, triées par date de création : une commande oubliée la veille ne disparaît pas du
    /// tableau. Une commande servie ou annulée d'un jour précédent n'y figure pas.
    /// Le total est la somme des quantités × prix figés des lignes, non arrondie.
    /// </summary>
    public async Task<IReadOnlyList<OrderSummary>> ListForDayAsync(DateTime day)
    {
        await using var db = await factory.CreateDbContextAsync();
        var calendar = await RestaurantCalendar.ForCurrentRestaurantAsync(db, _clock);
        var (start, end) = calendar.UtcBounds(UtcDays.Of(day));

        var orders = await db.Orders.AsNoTracking()
            .Include(o => o.Lines.OrderBy(l => l.Id))
            .Where(o => (o.CreatedAt >= start && o.CreatedAt < end) || (o.CreatedAt < start && OpenStatuses.Contains(o.Status)))
            .ToListAsync();

        var ordered = orders.Where(o => o.CreatedAt < start).OrderBy(o => o.CreatedAt).ThenBy(o => o.Number)
            .Concat(orders.Where(o => o.CreatedAt >= start).OrderBy(o => o.Number))
            .ToList();

        return await SummarizeAsync(db, ordered);
    }

    /// <summary>La commande (et ses lignes, dans l'ordre d'enregistrement) du restaurant courant, quel que soit son jour ou son statut ; null si elle n'existe pas.</summary>
    public async Task<OrderSummary?> GetAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Lines.OrderBy(l => l.Id))
            .FirstOrDefaultAsync(o => o.Id == id);

        return order is null ? null : (await SummarizeAsync(db, [order]))[0];
    }

    private static async Task<List<OrderSummary>> SummarizeAsync(ChefOsDbContext db, List<Order> orders)
    {
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
                o.Note,
                o.Lines.Sum(l => l.Quantity * l.UnitPriceInclVat),
                o.Lines.Select(l => new OrderLineSummary(l.RecipeId, names[l.RecipeId], l.Quantity, l.Note)).ToList()))
            .ToList();
    }

    /// <summary>
    /// Passe le statut de <paramref name="from"/> à <paramref name="to"/> en une instruction, uniquement si la commande est encore à <paramref name="from"/> :
    /// si un autre appel l'a déjà changé, aucune ligne n'est modifiée et le changement est refusé.
    /// </summary>
    private async Task ChangeStatusAsync(ChefOsDbContext db, int orderId, OrderStatus from, OrderStatus to)
    {
        var changedAt = Now;
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

    internal static bool IsOrderNumberConflict(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: OrderNumberIndex };

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Ligne de commande aux prix déjà figés (pré-commande d'une réservation).</summary>
internal sealed record FrozenOrderLine(int RecipeId, int Quantity, decimal UnitPriceInclVat, decimal VatRate, string? Note);
