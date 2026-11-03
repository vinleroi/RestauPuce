using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using ChefOS.Core.Receipts;

namespace ChefOS.Infrastructure.Receipts;

/// <summary>
/// Lit un ticket ou une facture (photo JPEG, PNG, WebP ou PDF) avec Claude : le fichier et la consigne partent dans un seul message, et la réponse est
/// forcée au format de <see cref="ReceiptExtractionJson.Schema"/> (sortie structurée), puis lue par <see cref="ReceiptExtractionJson.Parse"/>.
/// Appel sur le point d'accès bêta pour le repli côté serveur (<c>fallbacks: "default"</c>) : si le modèle décline la demande pour une raison de politique,
/// l'API la confie elle-même à son modèle de repli. Claude Opus 5.5 réfléchit toujours (réflexion adaptative) : pas de paramètre <c>thinking</c>,
/// seul l'effort (<see cref="Effort.Medium"/>) règle la profondeur ; ni température, ni préremplissage de la réponse.
/// Les erreurs de l'API sont traduites en <see cref="InvalidOperationException"/> au message français : <see cref="Services.ReceiptService"/> le garde
/// tel quel sur le ticket en échec.
/// </summary>
public sealed class ClaudeReceiptReader(AnthropicClient client) : IReceiptReader
{
    /// <summary>Délai d'un appel complet à l'API (la valeur par défaut du SDK est de dix minutes, trop longue pour un écran qui attend).</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);

    private const string Model = "claude-opus-5-5";
    private const long MaxTokens = 16000;

    internal const string RefusedMessage = "La lecture du ticket a été refusée";
    internal const string TooLongMessage = "Ticket trop long pour être lu";

    public const string Prompt = """
        Voici le ticket ou la facture d'un fournisseur de restaurant (photo ou PDF). Lis-le et renvoie son contenu au format demandé.

        - Recopie chaque libellé exactement comme il est imprimé, sans le corriger ni le développer.
        - amounts_include_vat vaut true si les prix et les totaux du document sont TTC (TVA comprise), false s'ils sont hors TVA (HTVA). Tous les montants de la réponse sont dans cette même base.
        - Convertis les conditionnements en quantité totale : « 6 x 1L » donne 6 avec l'unité l, « 2 x 2,5 kg » donne 5 avec l'unité kg, « 24 x 33cl » donne 7,92 avec l'unité l.
        - Pour une ligne vendue au poids, l'unité est kg ou g. Pour une ligne à la pièce ou au colis sans poids indiqué, l'unité est piece.
        - unit_price est le prix d'une unité de la quantité convertie (le prix du kg quand la quantité est en kg) ; line_total est le total de la ligne tel qu'imprimé.
        - vat_rate est une fraction : 0.06 pour 6 %, 0.21 pour 21 %. Mets null si le taux de la ligne n'est pas indiqué.
        - Omets les lignes de remise, de consigne, de sous-total, de total et de TVA : ne garde que les articles.
        - La date est au format yyyy-MM-dd.
        - Écris les nombres avec un point décimal, sans séparateur de milliers ni symbole monétaire.
        - Quand une valeur est absente ou illisible, mets null : n'invente rien.
        """;

    public async Task<ReceiptExtraction> ReadAsync(byte[] content, string contentType, CancellationToken ct = default)
    {
        var request = BuildRequest(content, contentType);

        BetaMessage response;
        try
        {
            response = await client.Beta.Messages.Create(request, ct);
        }
        catch (AnthropicUnauthorizedException ex)
        {
            throw new InvalidOperationException("Clé API Anthropic refusée (invalide ou révoquée) : vérifiez Anthropic:ApiKey (voir le README)", ex);
        }
        catch (AnthropicForbiddenException ex)
        {
            throw new InvalidOperationException("Cette clé API Anthropic n'a pas le droit de lire des tickets : vérifiez ses autorisations", ex);
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
            // Fichier illisible ou trop lourd (une image de plus de 5 Mo est refusée), mais aussi appel mal formé : le motif de l'API est repris tel quel.
            throw new InvalidOperationException($"Anthropic a refusé la requête : {ApiReason(ex)}", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new InvalidOperationException($"Erreur Anthropic (HTTP {(int)ex.StatusCode}) : la lecture du ticket a échoué", ex);
        }
        catch (AnthropicIOException ex)
        {
            throw new InvalidOperationException("Impossible de joindre Anthropic : vérifiez la connexion du serveur", ex);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Délai du client dépassé (le SDK le signale par une annulation) : ce n'est pas l'appelant qui annule.
            throw new InvalidOperationException("Anthropic met trop de temps à répondre : réessayez dans un instant", ex);
        }

        return ReadResponse(response);
    }

    /// <summary>
    /// Motif donné par l'API (<c>error.message</c>). Le SDK ne l'expose que dans le texte de l'exception (« Status Code: BadRequest » puis le corps JSON) :
    /// on le relit de là, et tout ce qui ne ressemble pas à ce format donne un texte générique.
    /// </summary>
    private static string ApiReason(AnthropicApiException ex)
    {
        const string fallback = "requête invalide";

        var start = ex.Message.IndexOf('{');
        if (start < 0)
            return fallback;

        try
        {
            using var body = JsonDocument.Parse(ex.Message[start..]);
            return body.RootElement.TryGetProperty("error", out var error)
                   && error.TryGetProperty("message", out var message)
                   && message.ValueKind == JsonValueKind.String
                   && message.GetString() is { Length: > 0 } reason
                ? reason
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static MessageCreateParams BuildRequest(byte[] content, string contentType)
    {
        var data = Convert.ToBase64String(content);

        BetaContentBlockParam file = contentType.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" => Image(data, MediaType.ImageJpeg),
            "image/png" => Image(data, MediaType.ImagePng),
            "image/webp" => Image(data, MediaType.ImageWebP),
            "application/pdf" => new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = data } },
            _ => throw new InvalidOperationException("Format non pris en charge (JPEG, PNG, WebP ou PDF)")
        };

        return new MessageCreateParams
        {
            Model = Model,
            MaxTokens = MaxTokens,
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Medium,
                Format = new BetaJsonOutputFormat { Schema = ReceiptExtractionJson.Schema }
            },
            Messages =
            [
                new BetaUserMessageParam
                {
                    Content = new List<BetaContentBlockParam> { file, new BetaTextBlockParam { Text = Prompt } }
                }
            ]
        };
    }

    private static BetaImageBlockParam Image(string base64, MediaType mediaType) =>
        new() { Source = new BetaBase64ImageSource { Data = base64, MediaType = mediaType } };

    private static ReceiptExtraction ReadResponse(BetaMessage response)
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

        return ReceiptExtractionJson.Parse(json ?? string.Empty);
    }
}
