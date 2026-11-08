using ChefOS.Infrastructure.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

/// <summary>Table globale (pas de restaurant, pas de filtre) : la passerelle de démonstration est partagée entre le back et la vitrine.</summary>
internal sealed class DemoGatewayPaymentConfiguration : IEntityTypeConfiguration<DemoGatewayPayment>
{
    public void Configure(EntityTypeBuilder<DemoGatewayPayment> builder)
    {
        builder.ToTable("DemoGatewayPayments");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasMaxLength(64);
        builder.Property(p => p.Amount).HasPrecision(18, 2);
        builder.Property(p => p.RefundedAmount).HasPrecision(18, 2);
        builder.Property(p => p.Description).HasMaxLength(500);
        builder.Property(p => p.RedirectUrl).HasMaxLength(2000);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.RefundKeys).HasColumnType("text[]");
    }
}
