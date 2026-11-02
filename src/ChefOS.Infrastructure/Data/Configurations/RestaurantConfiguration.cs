using ChefOS.Core.Restaurants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class RestaurantConfiguration : IEntityTypeConfiguration<Restaurant>
{
    public void Configure(EntityTypeBuilder<Restaurant> builder)
    {
        builder.HasIndex(r => r.Slug).IsUnique();

        builder.Property(r => r.DefaultFoodVatRate).HasPrecision(5, 4);
        builder.Property(r => r.DefaultDrinkVatRate).HasPrecision(5, 4);
        builder.Property(r => r.FoodCostAlertThreshold).HasPrecision(5, 4);
    }
}
