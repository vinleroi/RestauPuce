using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

/// <summary>Profil public (vitrine) d'un restaurant et règles de réservation. Relation 1-1 avec le restaurant.</summary>
public class VitrineProfile : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public bool IsPublished { get; set; }
    public string? Tagline { get; set; }
    public string? AmbianceText { get; set; }
    /// <summary>Couleur d'accent au format #RRGGBB.</summary>
    public string? AccentColor { get; set; }
    public int? CoverImageId { get; set; }
    public string? City { get; set; }
    public string? Phone { get; set; }
    public string? PublicEmail { get; set; }

    public bool ReservationsOpen { get; set; }
    /// <summary>Au-delà de ce nombre de personnes, un prépaiement du pré-commande remplace l'acompte.</summary>
    public int GroupThreshold { get; set; } = 5;
    public decimal DepositPerPerson { get; set; } = 10m;
    public decimal PrepaymentPercent { get; set; } = 0.30m;
    public int FreeCancellationHours { get; set; } = 24;
    public int MinLeadMinutes { get; set; } = 120;
    public int MaxDaysAhead { get; set; } = 60;
    public int MaxPartySize { get; set; } = 20;
    public int PendingPaymentMinutes { get; set; } = 30;
}
