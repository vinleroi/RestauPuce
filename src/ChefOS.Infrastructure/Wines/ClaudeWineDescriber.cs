using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;

namespace ChefOS.Infrastructure.Wines;

/// <summary>
/// Rédige la fiche d'un vin avec Claude, sur le modèle de <see cref="Receipts.ClaudeReceiptReader"/> : message texte seul, réponse forcée au format de
/// <see cref="WineDescriptionJson.Schema"/> (sortie structurée), lue par <see cref="WineDescriptionJson.Parse"/>. Appel sur le point d'accès bêta pour le repli
/// côté serveur (<c>fallbacks: "default"</c>) ; Claude Opus 5.5 réfléchit toujours (réflexion adaptative) : pas de paramètre <c>thinking</c>, seul l'effort
/// (<see cref="Effort.Medium"/>) règle la profondeur ; ni température, ni préremplissage.
/// Les erreurs de l'API sont traduites en <see cref="InvalidOperationException"/> au message français, affiché tel quel au chef.
/// </summary>
public sealed class ClaudeWineDescriber(IAnthropicClient client) : IWineDescriber
{
    private const string Model = "claude-opus-5-5";
    private const long MaxTokens = 16000;

    /// <summary>Délai de chaque essai (un seul nouvel essai est permis).</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);

    internal const string RefusedMessage = "La rédaction de la fiche a été refusée";
    internal const string TooLongMessage = "Fiche trop longue pour être rédigée";

    public const string Prompt = """
        Tu es un sommelier expérimenté. Rédige la fiche du vin ci-dessous pour la carte des vins publique d'un restaurant, au format demandé et en français.

        - N'invente jamais de producteur, de domaine, de note ni de récompense : décris le vin à partir de son nom, de son appellation, de sa couleur et de son type.
        - Pour le millésime, reste prudent : parle de « caractéristiques typiques du millésime » sans affirmer de détail précis sur cette année-là.
        - Rédige 2 à 3 phrases pour chacun des champs robe, nez, bouche, garde et general_pairings ; le ton est élégant, simple et sans jargon inutile.
        - Propose des accords uniquement avec les plats de la liste fournie : recopie leurs noms exactement. Si aucun plat ne convient, ou si la liste est vide, renvoie un tableau vide.
        - Si le vin est inconnu ou ambigu, donne une description générique prudente pour l'appellation et la couleur les plus probables, et ne dis rien de faux.
        - appellation vaut null quand elle est inconnue ; service_temperature s'écrit comme « 16-18 °C ».
        """;

    /// <summary>Le client partagé, avec le délai de ce describer : le SDK en fait une copie qui garde le même transport.</summary>
    public static IAnthropicClient Configure(AnthropicClient shared) =>
        shared.WithOptions(options => options with { Timeout = RequestTimeout });

    public async Task<WineDraft> DescribeAsync(WineDescribeRequest request, CancellationToken ct = default)
    {
        var parameters = BuildRequest(request);

        try
        {
            var response = await client.Beta.Messages.Create(parameters, ct);

            // Lu dans le try : le contenu de la réponse n'est décodé qu'à l'accès, et une réponse mal formée lève une exception du SDK.
            return ReadResponse(response);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // L'annulation demandée par l'appelant n'est jamais traduite, même si le SDK l'a enveloppée dans une de ses exceptions.
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new InvalidOperationException("Clé API Anthropic refusée (invalide ou révoquée) : vérifiez Anthropic:ApiKey (voir le README)", ex);
        }
        catch (AnthropicForbiddenException ex)
        {
            throw new InvalidOperationException("Cette clé API Anthropic n'a pas le droit de rédiger des fiches : vérifiez ses autorisations", ex);
        }
        catch (AnthropicRateLimitException ex)
        {
            throw new InvalidOperationException("Quota Anthropic atteint : réessayez dans un instant", ex);
        }
        catch (Anthropic5xxException ex)
        {
            throw new InvalidOperationException("Le service Anthropic est indisponible ou surchargé : réessayez dans un instant", ex);
        }
        catch (AnthropicApiException ex) when ((int)ex.StatusCode == 402)
        {
            throw new InvalidOperationException("Crédit Anthropic épuisé ou paiement requis : vérifiez la facturation du compte Anthropic", ex);
        }
        catch (AnthropicNotFoundException ex)
        {
            throw new InvalidOperationException("Modèle Anthropic introuvable ou non accessible avec cette clé", ex);
        }
        catch (AnthropicBadRequestException ex)
        {
            // Le motif de l'API (en anglais) reste dans l'exception d'origine, pour les journaux.
            throw new InvalidOperationException("Anthropic a refusé la requête de rédaction de la fiche", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new InvalidOperationException($"Erreur Anthropic (HTTP {(int)ex.StatusCode}) : la rédaction de la fiche a échoué", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new InvalidOperationException("Impossible de joindre Anthropic : vérifiez la connexion du serveur", ex);
        }
        catch (OperationCanceledException ex)
        {
            // Délai du client dépassé (le SDK le signale par une annulation) : ce n'est pas l'appelant qui annule.
            throw new InvalidOperationException("Anthropic met trop de temps à répondre : réessayez dans un instant", ex);
        }
        catch (Exception ex) when (ex is AnthropicException or JsonException)
        {
            // Réponse 200 mal formée (JSON invalide, champ obligatoire absent) : AnthropicInvalidDataException ou JsonException.
            throw new InvalidOperationException(WineDescriptionJson.UnreadableMessage, ex);
        }
    }

    /// <summary>Le texte envoyé : la consigne, puis le vin et les plats proposables.</summary>
    public static string BuildMessage(WineDescribeRequest request)
    {
        var text = new StringBuilder(Prompt).AppendLine().AppendLine();
        text.AppendLine($"Nom du vin : {request.Name.Trim()}");
        text.AppendLine($"Millésime : {(request.Vintage is { } vintage ? vintage.ToString() : "non précisé")}");
        text.AppendLine($"Producteur : {(string.IsNullOrWhiteSpace(request.Producer) ? "non précisé" : request.Producer.Trim())}");
        text.AppendLine($"Appellation : {(string.IsNullOrWhiteSpace(request.Appellation) ? "non précisée" : request.Appellation.Trim())}");
        text.AppendLine();
        text.AppendLine("Plats de la carte pour les accords :");
        if (request.DishNames.Count == 0)
            text.AppendLine("(aucun)");
        foreach (var dish in request.DishNames)
            text.AppendLine($"- {dish}");

        return text.ToString();
    }

    private static MessageCreateParams BuildRequest(WineDescribeRequest request) => new()
    {
        Model = Model,
        MaxTokens = MaxTokens,
        Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
        Fallbacks = new Default(),
        OutputConfig = new BetaOutputConfig
        {
            Effort = Effort.Medium,
            Format = new BetaJsonOutputFormat { Schema = WineDescriptionJson.Schema }
        },
        Messages = [new BetaUserMessageParam { Content = BuildMessage(request) }]
    };

    private static WineDraft ReadResponse(BetaMessage response)
    {
        // Le statut avant le contenu : un refus ou une réponse coupée n'a pas de JSON exploitable.
        if (response.StopReason == "refusal")
            throw new InvalidOperationException(RefusedMessage);
        if (response.StopReason == "max_tokens")
            throw new InvalidOperationException(TooLongMessage);

        // Le dernier bloc de texte est la réponse : les blocs de réflexion ou de repli éventuels la précèdent.
        string? json = null;
        foreach (var block in response.Content)
        {
            if (block.TryPickText(out var text))
                json = text.Text;
        }

        return WineDescriptionJson.Parse(json ?? string.Empty);
    }
}
