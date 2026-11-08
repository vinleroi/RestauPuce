using ChefOS.Infrastructure.Services;
using ChefOS.Vitrine.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ChefOS.Vitrine.Endpoints;

/// <summary>Repère d'un endpoint non HTML : ses erreurs restent des codes HTTP nus, sans page d'erreur réexécutée (un POST ne rend pas une page).</summary>
public sealed record ApiEndpoint;

/// <summary>
/// Endpoints HTTP du site public. Tous vivent sous <c>/{slug}</c> : <see cref="Tenancy.SlugTenantMiddleware"/> répond 404 à un slug inconnu ou
/// non publié avant d'arriver ici, et fixe le restaurant courant (donc les lectures ne voient que ses données).
/// </summary>
public static class VitrineEndpoints
{
    public static void MapVitrineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/{slug}/images/{id:int}", GetImageAsync)
            .WithMetadata(new ApiEndpoint());

        // Retour de Mollie (formulaire « id=tr_… ») : relit le statut chez la passerelle. 200 pour une requête sans identifiant ou un paiement inconnu
        // (réessayer ne corrigerait rien) ; 503 si la synchronisation échoue de façon inattendue (journalisée), pour que Mollie réessaie et qu'un paiement réglé
        // ne soit pas perdu. Pas de limitation par IP : un webhook légitime peut se répéter, et rien n'est cru de ce qui est reçu (le statut est relu chez la
        // passerelle). Pas de jeton anti-falsification : l'appelant est un serveur.
        endpoints.MapPost("/{slug}/webhooks/mollie", PostMollieWebhookAsync)
            .DisableAntiforgery()
            .WithMetadata(new ApiEndpoint());
    }

    private static async Task<IResult> GetImageAsync(int id, HttpContext context, [FromServices] VitrineService vitrine)
    {
        // Publiée seulement, et du restaurant du lien (contexte filtré) : l'image d'un autre restaurant ou d'une vitrine non publiée répond 404.
        var image = await vitrine.GetPublicImageAsync(id);
        if (image is null)
            return TypedResults.NotFound();

        var headers = context.Response.Headers;
        // Type fixé à l'enregistrement (JPEG, PNG ou WebP) et affiché tel quel : jamais deviné par le navigateur.
        headers.XContentTypeOptions = "nosniff";
        headers.CacheControl = "public, max-age=86400";
        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(image.FileName);
        headers.ContentDisposition = disposition.ToString();

        return TypedResults.File(image.Data, image.ContentType);
    }

    private static async Task<IResult> PostMollieWebhookAsync(HttpContext context, [FromServices] PaymentService payments,
        [FromServices] ILogger<VitrineProgram> logger)
    {
        try
        {
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                var id = form["id"].ToString().Trim();
                if (id.Length is > 0 and <= 100)
                    await payments.SyncAsync(id, context.RequestAborted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Webhook de paiement non traité : Mollie réessaiera");
            // Un code nu : sans cela, la page « introuvable » serait réexécutée sur ce POST (et répondrait 400 au lieu de 503).
            context.Features.Get<Microsoft.AspNetCore.Diagnostics.IStatusCodePagesFeature>()?.Enabled = false;
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return TypedResults.Ok();
    }
}
