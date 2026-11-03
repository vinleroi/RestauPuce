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
    private const string Model = "claude-opus-5-5";
    private const long MaxTokens = 16000;

    // L'API refuse une image de plus de 5 Mo : on l'annonce sans l'envoyer (la page de téléversement réduit déjà les photos dans le navigateur).
    private const int MaxImageBytes = 5 * 1024 * 1024;

    internal const string RefusedMessage = "La lecture du ticket a été refusée";
    internal const string TooLongMessage = "Ticket trop long pour être lu";
    internal const string ImageTooLargeMessage = "Image trop volumineuse pour la lecture (5 Mo maximum) : reprenez la photo ou réduisez-la";

    public const string Prompt = """
        Voici le ticket ou la facture d'un fournisseur de restaurant (photo ou PDF). Lis-le et renvoie son contenu au format demandé.

        - Recopie chaque libellé exactement comme il est imprimé, sans le corriger ni le développer.
        - amounts_include_vat vaut true si les prix et les totaux du document sont TTC (TVA comprise), false s'ils sont hors TVA (HTVA). Tous les montants de la réponse sont dans cette même base.
        - Convertis les conditionnements en quantité totale : « 6 x 1L » donne 6 avec l'unité l, « 2 x 2,5 kg » donne 5 avec l'unité kg, « 24 x 33cl » donne 7,92 avec l'unité l.
        - Quand la quantité commandée multiplie un conditionnement du libellé, multiplie-les : 2 commandés de « FILET OIGNONS 5KG » donnent 10 avec l'unité kg.
        - Pour une ligne vendue au poids, l'unité est kg ou g. Pour une ligne à la pièce ou au colis sans poids indiqué, l'unité est piece.
        - unit_price est le prix d'une unité de la quantité convertie (le prix du kg quand la quantité est en kg) ; line_total est le total de la ligne tel qu'imprimé.
        - vat_rate est une fraction : 0.06 pour 6 %, 0.21 pour 21 %. Quand la TVA est donnée par un code (A, B, C…), retrouve le taux de chaque ligne avec la légende imprimée sur le ticket ; sans légende lisible, mets null.
        - Omets les lignes de remise, de consigne, de vidange, de sous-total, de total et de TVA : ne garde que les articles.
        - Les dates sont imprimées jour/mois/année : 03/11/2026 ou 03/11/26 est le 3 novembre 2026, pas le 11 mars. Renvoie-la au format yyyy-MM-dd, et mets null si l'année n'est pas imprimée.
        - Écris les nombres avec un point décimal, sans séparateur de milliers ni symbole monétaire.
        - Quand une valeur est absente ou illisible, mets null : n'invente rien.
        """;

    public async Task<ReceiptExtraction> ReadAsync(byte[] content, string contentType, CancellationToken ct = default)
    {
        var request = BuildRequest(content, contentType);

        try
        {
            var response = await client.Beta.Messages.Create(request, ct);

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
            // Le motif de l'API (en anglais) reste dans l'exception d'origine, pour les journaux.
            throw new InvalidOperationException("Anthropic a refusé la requête (format ou taille du fichier non accepté)", ex);
        }
        catch (AnthropicApiException ex)
        {
            throw new InvalidOperationException($"Erreur Anthropic (HTTP {(int)ex.StatusCode}) : la lecture du ticket a échoué", ex);
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
            throw new InvalidOperationException(ReceiptExtractionJson.UnreadableMessage, ex);
        }
    }

    private static MessageCreateParams BuildRequest(byte[] content, string contentType)
    {
        var type = contentType.Trim().ToLowerInvariant();
        if (type is not ("image/jpeg" or "image/png" or "image/webp" or "application/pdf"))
            throw new InvalidOperationException("Format non pris en charge (JPEG, PNG, WebP ou PDF)");
        if (type != "application/pdf" && content.Length > MaxImageBytes)
            throw new InvalidOperationException(ImageTooLargeMessage);

        var data = Convert.ToBase64String(content);

        BetaContentBlockParam file = type switch
        {
            "image/jpeg" => Image(data, MediaType.ImageJpeg),
            "image/png" => Image(data, MediaType.ImagePng),
            "image/webp" => Image(data, MediaType.ImageWebP),
            _ => new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = data } }
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
