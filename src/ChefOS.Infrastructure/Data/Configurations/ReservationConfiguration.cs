using ChefOS.Core.Vitrine;
using ChefOS.Core.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(r => r.PublicToken).HasMaxLength(64);
        builder.Property(r => r.ServiceName).HasMaxLength(100);
        builder.Property(r => r.CustomerName).HasMaxLength(200);
        builder.Property(r => r.CustomerEmail).HasMaxLength(200);
        builder.Property(r => r.CustomerPhone).HasMaxLength(50);
        builder.Property(r => r.Note).HasColumnType("text");
        builder.Property(r => r.Status).HasConversion<string>();
        builder.Property(r => r.AmountDue).HasPrecision(18, 2);
        builder.Property(r => r.PreorderTotalInclVat).HasPrecision(18, 2);

        // Le lien public identifie la réservation : unique sur toute la base, pas seulement par restaurant.
        builder.HasIndex(r => r.PublicToken)
            .IsUnique()
            .HasDatabaseName("IX_Reservations_PublicToken");

        // Recherche des réservations d'un restaurant par date.
        builder.HasIndex(r => new { r.RestaurantId, r.StartsAtUtc })
            .HasDatabaseName("IX_Reservations_Start");

        // Les convives disparaissent avec leur réservation.
        builder.HasMany(r => r.Guests)
            .WithOne()
            .HasForeignKey(g => g.ReservationId)
            .OnDelete(DeleteBehavior.Cascade);

        // La réservation survit à la suppression de la commande liée.
        builder.HasOne<Order>()
            .WithMany()
            .HasForeignKey(r => r.OrderId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
