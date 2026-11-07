using ChefOS.Core.Vitrine;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class ServiceScheduleConfiguration : IEntityTypeConfiguration<ServiceSchedule>
{
    public void Configure(EntityTypeBuilder<ServiceSchedule> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(s => s.Name).HasMaxLength(100);

        builder.HasIndex(s => new { s.RestaurantId, s.DayOfWeek, s.Name })
            .IsUnique()
            .HasDatabaseName("IX_ServiceSchedules_Key");

        builder.ToTable(t => t.HasCheckConstraint(
            "CK_ServiceSchedule_Times",
            "\"EndLocal\" > \"StartLocal\" AND \"Capacity\" > 0 AND \"SlotMinutes\" IN (15, 30, 60)"));
    }
}
