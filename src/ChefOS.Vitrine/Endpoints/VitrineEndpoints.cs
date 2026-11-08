using ChefOS.Infrastructure.Services;
using ChefOS.Vitrine.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ChefOS.Vitrine.Endpoints;

/// <summary>
/// Endpoints HTTP du site public. Tous vivent sous <c>/{slug}</c> : <see cref="Tenancy.SlugTenantMiddleware"/> répond 404 à un slug inconnu ou
/// non publié avant d'arriver ici, et fixe le restaurant courant (donc les lectures ne voient que ses données).
/// </summary>
/// <summary>Repère d'un endpoint non HTML : ses erreurs restent des codes HTTP nus, sans page d'erreur réexécutée (un POST ne rend pas une page).</summary>
public sealed record ApiEndpoint;

public static class VitrineEndpoints
{
    public const string RateLimitPolicy = "attempts";

    public static void MapVitrineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/{slug}/images/{id:int}", GetImageAsync)
            .WithMetadata(new ApiEndpoint());

        // Retour de Mollie (formulaire « id=tr_… ») : relit le statut chez la passerelle. Toujours 200, y compris sur erreur (journalisée) :
        // un autre code ferait réessayer Mollie sans rien corriger. Pas de jeton anti-falsification : l'appelant est un serveur, et rien n'est
        // cru de ce qui est reçu (le statut est relu chez la passerelle).
        endpoints.MapPost("/{slug}/webhooks/mollie", PostMollieWebhookAsync)
            .DisableAntiforgery()
            .RequireRateLimiting(RateLimitPolicy)
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
            logger.LogWarning("Webhook de paiement non traité : {Reason}", ex.Message);
        }

        return TypedResults.Ok();
    }
}
