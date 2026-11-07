using ChefOS.Core.Common;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;
using ChefOS.Infrastructure.Wines;

namespace ChefOS.Back.Components.Pages.Wines;

/// <summary>
/// Valeurs saisies dans l'éditeur d'un vin. Un prix vidé n'est pas un zéro : <see cref="ToInput"/> refuse le prix de la bouteille manquant plutôt
/// que d'enregistrer une valeur que le chef n'a pas écrite (les autres bornes sont celles du service).
/// Une fiche rédigée par l'assistant (<see cref="ApplyDraft"/>) lève <see cref="DescriptionIsAiDraft"/> ; modifier un texte à la main ne l'efface pas :
/// seule la case « J'ai relu la fiche » (<see cref="ConfirmAiDraftReviewed"/>) le fait, à l'enregistrement.
/// </summary>
public sealed class WineForm
{
    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public string? Producer { get; set; }
    public int? Vintage { get; set; }
    public string? Appellation { get; set; }
    public string Region { get; set; } = "";
    public string Country { get; set; } = "";
    public WineColor Color { get; set; } = WineColor.Rouge;
    public string Grapes { get; set; } = "";
    public string Robe { get; set; } = "";
    public string Nez { get; set; } = "";
    public string Bouche { get; set; } = "";
    public string Garde { get; set; } = "";
    public string ServiceTemperature { get; set; } = "";
    public string GeneralPairings { get; set; } = "";
    public string? PersonalNote { get; set; }
    public decimal? PriceBottle { get; set; }
    public decimal? PriceGlass { get; set; }
    public bool IsPublished { get; set; }

    /// <summary>La fiche vient de l'assistant et n'a pas encore été confirmée relue.</summary>
    public bool DescriptionIsAiDraft { get; set; }

    /// <summary>« J'ai relu la fiche » : la case n'existe que tant que <see cref="DescriptionIsAiDraft"/> est levé.</summary>
    public bool ConfirmAiDraftReviewed { get; set; }

    /// <summary>Plats accordés (identifiants de recettes).</summary>
    public HashSet<int> PairingRecipeIds { get; set; } = [];

    /// <summary>Le nom suffit à demander une fiche à l'assistant.</summary>
    public bool CanDraft => !string.IsNullOrWhiteSpace(Name);

    public static WineForm From(WineListItem wine, IEnumerable<int> pairingRecipeIds) => new()
    {
        Id = wine.Id,
        Name = wine.Name,
        Producer = wine.Producer,
        Vintage = wine.Vintage,
        Appellation = wine.Appellation,
        Region = wine.Region,
        Country = wine.Country,
        Color = wine.Color,
        Grapes = wine.Grapes,
        Robe = wine.Robe,
        Nez = wine.Nez,
        Bouche = wine.Bouche,
        Garde = wine.Garde,
        ServiceTemperature = wine.ServiceTemperature,
        GeneralPairings = wine.GeneralPairings,
        PersonalNote = wine.PersonalNote,
        PriceBottle = wine.PriceBottle,
        PriceGlass = wine.PriceGlass,
        IsPublished = wine.IsPublished,
        DescriptionIsAiDraft = wine.DescriptionIsAiDraft,
        PairingRecipeIds = [.. pairingRecipeIds]
    };

    /// <summary>
    /// Remplit la fiche avec le brouillon de l'assistant (les champs de la fiche sont remplacés, une appellation absente du brouillon est conservée),
    /// ajoute ses accords à ceux déjà choisis, lève <see cref="DescriptionIsAiDraft"/> et décoche la confirmation : la fiche est à relire.
    /// </summary>
    public void ApplyDraft(WineDraft draft)
    {
        Appellation = draft.Appellation ?? Appellation;
        Region = draft.Region;
        Country = draft.Country;
        Color = draft.Color;
        Grapes = draft.Grapes;
        Robe = draft.Robe;
        Nez = draft.Nez;
        Bouche = draft.Bouche;
        Garde = draft.Garde;
        ServiceTemperature = draft.ServiceTemperature;
        GeneralPairings = draft.GeneralPairings;
        PairingRecipeIds.UnionWith(draft.SuggestedPairingRecipeIds);

        DescriptionIsAiDraft = true;
        ConfirmAiDraftReviewed = false;
    }

    /// <exception cref="DomainValidationException">Le prix de la bouteille est vide.</exception>
    public WineInput ToInput() => new(
        Id,
        Name,
        Producer,
        Vintage,
        Appellation,
        Region,
        Country,
        Color,
        Grapes,
        Robe,
        Nez,
        Bouche,
        Garde,
        ServiceTemperature,
        GeneralPairings,
        PersonalNote,
        PriceBottle ?? throw new DomainValidationException("Le prix de la bouteille est obligatoire"),
        PriceGlass,
        IsPublished,
        DescriptionIsAiDraft,
        DescriptionIsAiDraft && ConfirmAiDraftReviewed,
        PairingRecipeIds.Order().ToList());
}
