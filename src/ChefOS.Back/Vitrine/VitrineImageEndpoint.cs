using ChefOS.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ChefOS.Back.Vitrine;

/// <summary>
/// <c>GET /vitrine/images/{id}</c> : une image de la vitrine, pour l'aperçu des réglages (publiée ou non).
/// Utilisateur connecté requis ; la lecture passe par <see cref="VitrineService.GetImageAsync"/>, donc filtrée par restaurant :
/// l'image d'un autre restaurant, ou inexistante, répond 404.
/// </summary>
public static class VitrineImageEndpoint
{
    public static string RouteFor(int imageId) => $"/vitrine/images/{imageId}";

    public static IEndpointConventionBuilder MapVitrineImageEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/vitrine/images/{id:int}", GetAsync).RequireAuthorization();

    private static async Task<IResult> GetAsync(int id, HttpContext context, [FromServices] VitrineService vitrine)
    {
        var image = await vitrine.GetImageAsync(id);
        if (image is null)
            return TypedResults.NotFound();

        var headers = context.Response.Headers;
        // Type fixé à l'enregistrement (JPEG, PNG ou WebP) et affiché tel quel : jamais deviné par le navigateur.
        headers.XContentTypeOptions = "nosniff";
        // Photo du restaurant : pas de cache partagé, et une revalidation à chaque affichage.
        headers.CacheControl = "private, no-cache";

        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(image.FileName);
        headers.ContentDisposition = disposition.ToString();

        return TypedResults.File(image.Data, image.ContentType);
    }
}
