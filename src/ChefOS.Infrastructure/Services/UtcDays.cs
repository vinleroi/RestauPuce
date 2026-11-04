namespace ChefOS.Infrastructure.Services;

/// <summary>Jours du calendrier UTC : la normalisation des dates saisies et la règle « pas dans le futur », communes aux services.</summary>
internal static class UtcDays
{
    /// <summary>Minuit UTC du jour de <paramref name="value"/> ; une date sans fuseau est lue comme étant en UTC.</summary>
    public static DateTime Of(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return DateTime.SpecifyKind(utc.Date, DateTimeKind.Utc);
    }

    /// <summary>
    /// Vrai pour un jour postérieur à demain (UTC). Demain reste permis : selon le fuseau du chef, il peut déjà y être. Une date plus lointaine est une
    /// erreur de saisie ou de lecture (année mal lue) : prise comme référence, elle rendrait « en retard » tous les tickets réels suivants,
    /// dont les prix n'iraient plus qu'à l'historique.
    /// </summary>
    public static bool IsInTheFuture(DateTime day) => day.Date > DateTime.UtcNow.Date.AddDays(1);
}
