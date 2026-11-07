using ChefOS.Back.Components.Pages.Vitrine;
using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;

namespace ChefOS.Tests.Back;

public class VitrineSettingsFormTests
{
    private static VitrineSettingsForm Filled() => VitrineSettingsForm.From(new VitrineProfile(), "Europe/Brussels");

    [Fact]
    public void From_Defaults_ShowsThePrepaymentInPercent()
    {
        var form = Filled();

        Assert.Equal(30m, form.PrepaymentPercent);
        Assert.Equal(5, form.GroupThreshold);
        Assert.Equal(10m, form.DepositPerPerson);
        Assert.Equal("Europe/Brussels", form.TimeZoneId);
        Assert.False(form.IsPublished);
    }

    [Fact]
    public void ToInput_ConvertsThePercentBackToAFraction_AndCarriesEveryField()
    {
        var form = Filled();
        form.IsPublished = true;
        form.Tagline = "Belle table";
        form.AccentColor = "#112233";
        form.PrepaymentPercent = 12.5m;
        form.TimeZoneId = " Europe/Paris ";
        form.MaxPartySize = 40;

        var input = form.ToInput();

        Assert.True(input.IsPublished);
        Assert.Equal("Belle table", input.Tagline);
        Assert.Equal("#112233", input.AccentColor);
        Assert.Equal(0.125m, input.PrepaymentPercent);
        Assert.Equal("Europe/Paris", input.TimeZoneId);
        Assert.Equal(40, input.MaxPartySize);
        Assert.Equal(5, input.GroupThreshold);
    }

    [Theory]
    [InlineData("threshold", "Le seuil de groupe est obligatoire")]
    [InlineData("deposit", "L'acompte est obligatoire")]
    [InlineData("percent", "Le pourcentage de prépaiement est obligatoire")]
    [InlineData("cancel", "Le délai d'annulation gratuite est obligatoire")]
    [InlineData("lead", "Le délai minimal avant réservation est obligatoire")]
    [InlineData("ahead", "Le nombre de jours de réservation à l'avance est obligatoire")]
    [InlineData("max", "La taille maximale d'un groupe est obligatoire")]
    [InlineData("pending", "Le délai de paiement est obligatoire")]
    [InlineData("tz", "Le fuseau horaire est obligatoire")]
    public void ToInput_EmptyField_IsRefused(string field, string message)
    {
        var form = Filled();
        switch (field)
        {
            case "threshold": form.GroupThreshold = null; break;
            case "deposit": form.DepositPerPerson = null; break;
            case "percent": form.PrepaymentPercent = null; break;
            case "cancel": form.FreeCancellationHours = null; break;
            case "lead": form.MinLeadMinutes = null; break;
            case "ahead": form.MaxDaysAhead = null; break;
            case "max": form.MaxPartySize = null; break;
            case "pending": form.PendingPaymentMinutes = null; break;
            default: form.TimeZoneId = " "; break;
        }

        var ex = Assert.Throws<DomainValidationException>(form.ToInput);

        Assert.Equal(message, ex.Message);
    }

    [Theory]
    [InlineData("http://localhost:5108", "le-bistro", "http://localhost:5108/le-bistro")]
    [InlineData("http://localhost:5108/", " le-bistro ", "http://localhost:5108/le-bistro")]
    [InlineData(null, "le-bistro", null)]
    [InlineData("http://localhost:5108", " ", null)]
    public void PublicUrl_JoinsTheBaseUrlAndTheSlug(string? baseUrl, string? slug, string? expected) =>
        Assert.Equal(expected, VitrineSettingsForm.PublicUrl(baseUrl, slug));

    // ── Éditeur de services ──────────────────────────────────────────────────

    [Fact]
    public void Schedule_Add_ProposesLunchThenEvening()
    {
        var draft = new ScheduleDraft();

        var lunch = draft.Add(DayOfWeek.Monday);
        var evening = draft.Add(DayOfWeek.Monday);

        Assert.Equal(("Midi", new TimeSpan(12, 0, 0), new TimeSpan(14, 0, 0)), (lunch.Name, lunch.Start!.Value, lunch.End!.Value));
        Assert.Equal(("Soir", new TimeSpan(18, 30, 0), new TimeSpan(22, 0, 0)), (evening.Name, evening.Start!.Value, evening.End!.Value));
        Assert.Equal(2, draft.RowsOf(DayOfWeek.Monday).Count());
    }

    [Fact]
    public void Schedule_DuplicateToWeek_CopiesToTheOtherSixDays_SkippingSameName()
    {
        var draft = new ScheduleDraft();
        var monday = draft.Add(DayOfWeek.Monday);
        draft.Add(DayOfWeek.Friday).Name = "midi";

        var added = draft.DuplicateToWeek(monday);

        Assert.Equal(5, added);
        Assert.Equal(7, draft.Rows.Count);
        Assert.All(ScheduleDraft.Days, d => Assert.Single(draft.RowsOf(d)));
        Assert.Equal("midi", draft.RowsOf(DayOfWeek.Friday).Single().Name);
        Assert.NotSame(monday, draft.RowsOf(DayOfWeek.Tuesday).Single());
    }

    [Fact]
    public void Schedule_ToInput_ConvertsTimes_AndRoundTripsThroughFrom()
    {
        var draft = ScheduleDraft.From(
        [
            new ServiceSchedule { DayOfWeek = DayOfWeek.Sunday, Name = "Brunch", StartLocal = new TimeOnly(10, 15), EndLocal = new TimeOnly(15, 0), Capacity = 30, SlotMinutes = 15 },
            new ServiceSchedule { DayOfWeek = DayOfWeek.Monday, Name = "Soir", StartLocal = new TimeOnly(18, 0), EndLocal = new TimeOnly(22, 30), Capacity = 50, SlotMinutes = 60 }
        ]);

        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Sunday], draft.Rows.Select(r => r.Day));
        var inputs = draft.ToInput();

        Assert.Equal(new TimeOnly(18, 0), inputs[0].StartLocal);
        Assert.Equal(new TimeOnly(22, 30), inputs[0].EndLocal);
        Assert.Equal((DayOfWeek.Sunday, "Brunch", new TimeOnly(10, 15), 30, 15), (inputs[1].DayOfWeek, inputs[1].Name, inputs[1].StartLocal, inputs[1].Capacity, inputs[1].SlotMinutes));
    }

    [Fact]
    public void Schedule_ToInput_EmptyTimeOrCapacity_IsRefusedWithTheServiceName()
    {
        var draft = new ScheduleDraft();
        var row = draft.Add(DayOfWeek.Tuesday);
        row.End = null;
        var time = Assert.Throws<DomainValidationException>(draft.ToInput);
        Assert.Equal("Service Midi du mardi : indiquez l'heure de début et de fin", time.Message);

        row.End = new TimeSpan(14, 0, 0);
        row.Capacity = null;
        var capacity = Assert.Throws<DomainValidationException>(draft.ToInput);
        Assert.Equal("Service Midi du mardi : indiquez la capacité", capacity.Message);
    }
}
