using ChefOS.Core.Vitrine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(p => p.Kind).HasConversion<string>();
        builder.Property(p => p.Status).HasConversion<string>();
        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.RefundAmount).HasPrecision(18, 2);
        builder.Property(p => p.Gateway).HasMaxLength(50);
        builder.Property(p => p.GatewayPaymentId).HasMaxLength(100);
        builder.Property(p => p.CheckoutUrl).HasMaxLength(2000);

        // Le webhook et la synchronisation retrouvent un paiement par son identifiant chez la passerelle, dans un restaurant.
        builder.HasIndex(p => new { p.RestaurantId, p.GatewayPaymentId });

        builder.HasOne<Reservation>()
            .WithMany()
            .HasForeignKey(p => p.ReservationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
