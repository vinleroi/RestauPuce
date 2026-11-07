using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

/// <summary>Service récurrent (ex. « Dîner » le vendredi) : plage horaire locale, capacité en couverts et pas des créneaux.</summary>
public class ServiceSchedule : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public DayOfWeek DayOfWeek { get; set; }
    public string Name { get; set; } = string.Empty;
    public TimeOnly StartLocal { get; set; }
    /// <summary>Strictement après <see cref="StartLocal"/>.</summary>
    public TimeOnly EndLocal { get; set; }
    public int Capacity { get; set; }
    /// <summary>15, 30 ou 60.</summary>
    public int SlotMinutes { get; set; } = 30;
}
