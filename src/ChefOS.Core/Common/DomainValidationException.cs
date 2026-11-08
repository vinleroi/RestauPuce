namespace ChefOS.Core.Common;

/// <summary>
/// Erreur de validation métier au message français. <paramref name="field"/> (voir <see cref="ValidationFields"/>) dit à quel endroit du formulaire
/// la corriger, pour que l'interface route l'erreur sans lire le texte du message.
/// </summary>
public class DomainValidationException(string message, string? field = null) : Exception(message)
{
    public string? Field { get; } = field;
}

/// <summary>Zones d'un formulaire de réservation auxquelles une <see cref="DomainValidationException"/> peut se rapporter.</summary>
public static class ValidationFields
{
    /// <summary>Date, créneau, nombre de couverts, réservations fermées.</summary>
    public const string Slot = "slot";
    /// <summary>Convives et plats d'un grand groupe.</summary>
    public const string Guests = "guests";
    /// <summary>Nom, e-mail, téléphone, remarque.</summary>
    public const string Contact = "contact";
    /// <summary>Le montant affiché n'est plus celui du serveur.</summary>
    public const string Amount = "amount";
    /// <summary>La passerelle de paiement n'a pas pu démarrer le paiement.</summary>
    public const string Payment = "payment";
}
