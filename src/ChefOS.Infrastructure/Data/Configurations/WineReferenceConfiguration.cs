using ChefOS.Core.Vitrine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class WineReferenceConfiguration : IEntityTypeConfiguration<WineReference>
{
    public void Configure(EntityTypeBuilder<WineReference> builder)
    {
        builder.Property(w => w.Key).HasMaxLength(100);
        builder.Property(w => w.Name).HasMaxLength(200);
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
        builder.Property(w => w.Pairings).HasColumnType("text");

        builder.HasIndex(w => w.Key).IsUnique();
    }
}
