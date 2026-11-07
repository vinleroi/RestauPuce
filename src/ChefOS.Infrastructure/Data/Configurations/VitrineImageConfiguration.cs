using ChefOS.Core.Vitrine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class VitrineImageConfiguration : IEntityTypeConfiguration<VitrineImage>
{
    public void Configure(EntityTypeBuilder<VitrineImage> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(i => i.FileName).HasMaxLength(255);
        builder.Property(i => i.ContentType).HasMaxLength(100);
    }
}
