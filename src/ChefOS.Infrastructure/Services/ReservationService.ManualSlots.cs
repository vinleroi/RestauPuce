using ChefOS.Core.Vitrine;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

public partial class ReservationService
{
    /// <summary>
    /// Les heures de la grille du service ce jour-là (de son début à sa fin exclue, par pas de créneau), sans filtre de délai ni de capacité : le chef du back peut
    /// réserver un service complet ou un créneau passé. Vide si le service n'existe pas ce jour de la semaine. <see cref="CreateManualAsync"/> refuse toute heure hors grille.
    /// </summary>
    public async Task<IReadOnlyList<TimeOnly>> GetManualSlotsAsync(DateOnly date, string serviceName)
    {
        await using var db = await factory.CreateDbContextAsync();
        var schedule = await db.ServiceSchedules.AsNoTracking()
            .FirstOrDefaultAsync(s => s.DayOfWeek == date.DayOfWeek && s.Name == serviceName);
        if (schedule is null)
            return [];

        var step = Math.Max(schedule.SlotMinutes, 1);
        var slots = new List<TimeOnly>();
        for (var minutes = schedule.StartLocal.Hour * 60 + schedule.StartLocal.Minute; minutes < schedule.EndLocal.Hour * 60 + schedule.EndLocal.Minute; minutes += step)
            slots.Add(new TimeOnly(minutes / 60, minutes % 60));

        return slots;
    }
}
