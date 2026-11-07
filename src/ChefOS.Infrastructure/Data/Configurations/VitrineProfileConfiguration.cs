using ChefOS.Core.Vitrine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class VitrineProfileConfiguration : IEntityTypeConfiguration<VitrineProfile>
{
    public void Configure(EntityTypeBuilder<VitrineProfile> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(p => p.Tagline).HasMaxLength(200);
        builder.Property(p => p.AmbianceText).HasColumnType("text");
        builder.Property(p => p.AccentColor).HasMaxLength(7);
        builder.Property(p => p.City).HasMaxLength(100);
        builder.Property(p => p.Phone).HasMaxLength(50);
        builder.Property(p => p.PublicEmail).HasMaxLength(200);
        builder.Property(p => p.DepositPerPerson).HasPrecision(18, 2);
        builder.Property(p => p.PrepaymentPercent).HasPrecision(5, 4);

        // Un seul profil par restaurant.
        builder.HasIndex(p => p.RestaurantId).IsUnique();

        // Supprimer l'image de couverture ne supprime pas le profil.
        builder.HasOne<VitrineImage>()
            .WithMany()
            .HasForeignKey(p => p.CoverImageId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
