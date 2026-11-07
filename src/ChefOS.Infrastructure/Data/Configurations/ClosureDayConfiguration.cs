using ChefOS.Core.Vitrine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class ClosureDayConfiguration : IEntityTypeConfiguration<ClosureDay>
{
    public void Configure(EntityTypeBuilder<ClosureDay> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(c => c.Reason).HasMaxLength(200);

        builder.HasIndex(c => new { c.RestaurantId, c.DateLocal })
            .IsUnique()
            .HasDatabaseName("IX_ClosureDays_Key");
    }
}
