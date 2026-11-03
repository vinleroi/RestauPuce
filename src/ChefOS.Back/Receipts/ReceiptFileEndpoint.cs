using ChefOS.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace ChefOS.Back.Receipts;

/// <summary>
/// <c>GET /receipts/{id}/file</c> : le fichier téléversé d'un ticket (photo ou PDF), pour l'aperçu de l'écran de validation.
/// Utilisateur connecté requis ; la lecture passe par <see cref="ReceiptService.GetFileAsync"/>, donc filtrée par restaurant :
/// le ticket d'un autre restaurant, ou inexistant, répond 404.
/// </summary>
public static class ReceiptFileEndpoint
{
    public static string RouteFor(int receiptId) => $"/receipts/{receiptId}/file";

    public static IEndpointConventionBuilder MapReceiptFileEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/receipts/{id:int}/file", GetAsync).RequireAuthorization();

    private static async Task<IResult> GetAsync(int id, HttpContext context, [FromServices] ReceiptService receipts)
    {
        var file = await receipts.GetFileAsync(id);
        if (file is null)
            return TypedResults.NotFound();

        var headers = context.Response.Headers;
        // Type fixé à l'enregistrement (JPEG, PNG, WebP ou PDF) et affiché tel quel : jamais deviné par le navigateur.
        headers.XContentTypeOptions = "nosniff";
        // Document du restaurant : pas de cache partagé, et une revalidation à chaque affichage.
        headers.CacheControl = "private, no-cache";

        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(file.FileName);
        headers.ContentDisposition = disposition.ToString();

        return TypedResults.File(file.Data, file.ContentType);
    }
}
