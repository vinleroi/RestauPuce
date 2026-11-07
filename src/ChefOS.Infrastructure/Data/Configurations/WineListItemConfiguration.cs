using ChefOS.Core.Vitrine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class WineListItemConfiguration : IEntityTypeConfiguration<WineListItem>
{
    public void Configure(EntityTypeBuilder<WineListItem> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(w => w.Name).HasMaxLength(200);
        builder.Property(w => w.Producer).HasMaxLength(200);
        builder.Property(w => w.Appellation).HasMaxLength(200);
        builder.Property(w => w.Region).HasMaxLength(100);
        builder.Property(w => w.Country).HasMaxLength(100);
        builder.Property(w => w.Color).HasConversion<string>();
        builder.Property(w => w.Grapes).HasMaxLength(300);
        builder.Property(w => w.Robe).HasColumnType("text");
        builder.Property(w => w.Nez).HasColumnType("text");
        builder.Property(w => w.Bouche).HasColumnType("text");
        builder.Property(w => w.Garde).HasColumnType("text");
        builder.Property(w => w.ServiceTemperature).HasMaxLength(100);
        builder.Property(w => w.GeneralPairings).HasColumnType("text");
        builder.Property(w => w.PersonalNote).HasColumnType("text");
        builder.Property(w => w.PriceBottle).HasPrecision(18, 2);
        builder.Property(w => w.PriceGlass).HasPrecision(18, 2);

        // La carte garde ses vins si la fiche de la bibliothèque disparaît.
        builder.HasOne<WineReference>()
            .WithMany()
            .HasForeignKey(w => w.WineReferenceId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
