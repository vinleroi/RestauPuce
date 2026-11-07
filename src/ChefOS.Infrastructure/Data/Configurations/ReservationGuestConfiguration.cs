using ChefOS.Core.Vitrine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class ReservationGuestConfiguration : IEntityTypeConfiguration<ReservationGuest>
{
    public void Configure(EntityTypeBuilder<ReservationGuest> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(g => g.FirstName).HasMaxLength(100);
        builder.Property(g => g.LastName).HasMaxLength(100);

        // Les plats disparaissent avec leur convive ; la relation vers la réservation (cascade) est déclarée côté Reservation.Guests.
        builder.HasMany(g => g.Dishes)
            .WithOne()
            .HasForeignKey(d => d.GuestId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
