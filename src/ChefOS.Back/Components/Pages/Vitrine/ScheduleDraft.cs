using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;

namespace ChefOS.Back.Components.Pages.Vitrine;

/// <summary>Une ligne de l'éditeur de services. Les heures sont des durées depuis minuit (<c>MudTimePicker</c>) ; null : champ vide.</summary>
public sealed class ScheduleRow
{
    public DayOfWeek Day { get; set; }
    public string Name { get; set; } = "";
    public TimeSpan? Start { get; set; }
    public TimeSpan? End { get; set; }
    public int? Capacity { get; set; }
    public int SlotMinutes { get; set; } = 30;

    public ScheduleRow Copy(DayOfWeek day) => new()
    {
        Day = day, Name = Name, Start = Start, End = End, Capacity = Capacity, SlotMinutes = SlotMinutes
    };
}

/// <summary>
/// État de l'éditeur de services : lignes par jour, ajout, duplication sur la semaine, conversion en saisie du service.
/// Le chevauchement et les bornes sont vérifiés par le service à l'enregistrement.
/// </summary>
public sealed class ScheduleDraft
{
    /// <summary>Pas de créneaux proposés.</summary>
    public static IReadOnlyList<int> SlotChoices { get; } = [15, 30, 60];

    /// <summary>Lundi d'abord.</summary>
    public static IReadOnlyList<DayOfWeek> Days { get; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
    ];

    public static string DayLabel(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "Lundi",
        DayOfWeek.Tuesday => "Mardi",
        DayOfWeek.Wednesday => "Mercredi",
        DayOfWeek.Thursday => "Jeudi",
        DayOfWeek.Friday => "Vendredi",
        DayOfWeek.Saturday => "Samedi",
        DayOfWeek.Sunday => "Dimanche",
        _ => throw new ArgumentOutOfRangeException(nameof(day), day, null)
    };

    public List<ScheduleRow> Rows { get; } = [];

    public IEnumerable<ScheduleRow> RowsOf(DayOfWeek day) => Rows.Where(r => r.Day == day);

    public static ScheduleDraft From(IEnumerable<ServiceSchedule> schedules)
    {
        var draft = new ScheduleDraft();
        foreach (var s in schedules.OrderBy(s => Days.ToList().IndexOf(s.DayOfWeek)).ThenBy(s => s.StartLocal))
        {
            draft.Rows.Add(new ScheduleRow
            {
                Day = s.DayOfWeek, Name = s.Name, Start = s.StartLocal.ToTimeSpan(), End = s.EndLocal.ToTimeSpan(),
                Capacity = s.Capacity, SlotMinutes = s.SlotMinutes
            });
        }

        return draft;
    }

    /// <summary>Ajoute un service vide à <paramref name="day"/> (midi par défaut, le soir s'il y en a déjà un).</summary>
    public ScheduleRow Add(DayOfWeek day)
    {
        var evening = RowsOf(day).Any();
        var row = new ScheduleRow
        {
            Day = day,
            Name = evening ? "Soir" : "Midi",
            Start = evening ? new TimeSpan(18, 30, 0) : new TimeSpan(12, 0, 0),
            End = evening ? new TimeSpan(22, 0, 0) : new TimeSpan(14, 0, 0),
            Capacity = 40,
            SlotMinutes = 30
        };
        Rows.Add(row);
        return row;
    }

    public void Remove(ScheduleRow row) => Rows.Remove(row);

    /// <summary>
    /// Copie <paramref name="row"/> sur les six autres jours, sauf là où un service porte déjà ce nom. Renvoie le nombre de services ajoutés.
    /// </summary>
    public int DuplicateToWeek(ScheduleRow row)
    {
        var added = 0;
        foreach (var day in Days.Where(d => d != row.Day))
        {
            if (RowsOf(day).Any(r => string.Equals(r.Name.Trim(), row.Name.Trim(), StringComparison.InvariantCultureIgnoreCase)))
                continue;

            Rows.Add(row.Copy(day));
            added++;
        }

        return added;
    }

    /// <exception cref="DomainValidationException">Une ligne a une heure ou une capacité vide.</exception>
    public IReadOnlyList<ServiceScheduleInput> ToInput() =>
        Rows.Select(r =>
        {
            var label = string.IsNullOrWhiteSpace(r.Name) ? DayLabel(r.Day) : $"{r.Name.Trim()} du {DayLabel(r.Day).ToLowerInvariant()}";
            if (r.Start is not { } start || r.End is not { } end)
                throw new DomainValidationException($"Service {label} : indiquez l'heure de début et de fin");
            if (r.Capacity is not { } capacity)
                throw new DomainValidationException($"Service {label} : indiquez la capacité");

            return new ServiceScheduleInput(r.Day, r.Name, ToTime(start), ToTime(end), capacity, r.SlotMinutes);
        }).ToList();

    // Un TimeSpan du sélecteur reste dans la journée ; on tronque aux minutes.
    private static TimeOnly ToTime(TimeSpan value) => new(value.Hours, value.Minutes);
}
