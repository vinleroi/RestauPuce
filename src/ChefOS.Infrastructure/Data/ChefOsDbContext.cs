using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Sales;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Payments;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Data;

public class ChefOsDbContext(DbContextOptions<ChefOsDbContext> options, ITenantContext tenant)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();
    public DbSet<IngredientPrice> IngredientPrices => Set<IngredientPrice>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<RecipeLine> RecipeLines => Set<RecipeLine>();
    public DbSet<Receipt> Receipts => Set<Receipt>();
    public DbSet<ReceiptFile> ReceiptFiles => Set<ReceiptFile>();
    public DbSet<ReceiptLine> ReceiptLines => Set<ReceiptLine>();
    public DbSet<SupplierAlias> SupplierAliases => Set<SupplierAlias>();
    public DbSet<SalesEntry> SalesEntries => Set<SalesEntry>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<WasteEntry> WasteEntries => Set<WasteEntry>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<StockCountLine> StockCountLines => Set<StockCountLine>();
    public DbSet<VitrineProfile> VitrineProfiles => Set<VitrineProfile>();
    public DbSet<VitrineImage> VitrineImages => Set<VitrineImage>();
    public DbSet<ServiceSchedule> ServiceSchedules => Set<ServiceSchedule>();
    public DbSet<ClosureDay> ClosureDays => Set<ClosureDay>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationGuest> ReservationGuests => Set<ReservationGuest>();
    public DbSet<ReservationGuestDish> ReservationGuestDishes => Set<ReservationGuestDish>();
    public DbSet<Payment> Payments => Set<Payment>();
    /// <summary>Bibliothèque de vins partagée entre restaurants : volontairement sans filtre de restaurant.</summary>
    public DbSet<WineReference> WineReferences => Set<WineReference>();
    /// <summary>Paiements de la passerelle de démonstration : table globale, sans filtre de restaurant.</summary>
    public DbSet<DemoGatewayPayment> DemoGatewayPayments => Set<DemoGatewayPayment>();
    public DbSet<WineListItem> WineListItems => Set<WineListItem>();
    public DbSet<WineListItemPairing> WineListItemPairings => Set<WineListItemPairing>();

    // Propriété d'instance : EF la réévalue à chaque requête, donc chaque contexte filtre sur son propre restaurant.
    private int? CurrentRestaurantId => tenant.CurrentRestaurantId;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfigurationsFromAssembly(typeof(ChefOsDbContext).Assembly);

        builder.Entity<Supplier>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<Ingredient>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<IngredientPrice>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<Recipe>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<RecipeLine>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<Receipt>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<ReceiptFile>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<ReceiptLine>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<SupplierAlias>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<SalesEntry>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<Order>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<OrderLine>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<WasteEntry>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<StockCount>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<StockCountLine>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<VitrineProfile>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<VitrineImage>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<ServiceSchedule>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<ClosureDay>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<Reservation>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<ReservationGuest>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<ReservationGuestDish>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<Payment>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<WineListItem>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        builder.Entity<WineListItemPairing>().HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId);
        // WineReference : bibliothèque partagée, pas de RestaurantId, donc pas de filtre.
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnforceTenantRules();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnforceTenantRules();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Empêche d'écrire hors du restaurant courant : les ajouts sont estampillés (ou rejetés s'ils visent un autre
    /// restaurant), les modifications et suppressions d'une entité d'un autre restaurant sont rejetées.
    /// </summary>
    private void EnforceTenantRules()
    {
        var current = CurrentRestaurantId;

        foreach (var entry in ChangeTracker.Entries<ITenantScoped>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (current is null)
                        throw new InvalidOperationException(
                            $"Impossible d'ajouter un(e) {entry.Metadata.ShortName()} sans restaurant courant.");

                    if (entry.Entity.RestaurantId == 0)
                        entry.Entity.RestaurantId = current.Value;
                    else if (entry.Entity.RestaurantId != current.Value)
                        throw new InvalidOperationException(
                            $"Impossible d'ajouter un(e) {entry.Metadata.ShortName()} au restaurant {entry.Entity.RestaurantId} : le restaurant courant est {current}.");
                    break;

                case EntityState.Modified or EntityState.Deleted:
                    // La valeur d'origine est vérifiée aussi, pour qu'on ne puisse pas "voler" une entité en changeant son RestaurantId.
                    var restaurantId = entry.Property(nameof(ITenantScoped.RestaurantId));
                    if (entry.Entity.RestaurantId != current || (int)restaurantId.OriginalValue! != current)
                        throw new InvalidOperationException(
                            $"Impossible de modifier ou supprimer un(e) {entry.Metadata.ShortName()} d'un autre restaurant que le restaurant courant ({current?.ToString() ?? "aucun"}).");
                    break;
            }
        }
    }
}
