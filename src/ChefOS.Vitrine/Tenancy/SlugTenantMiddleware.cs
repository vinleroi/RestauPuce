using Microsoft.AspNetCore.Diagnostics;

namespace ChefOS.Vitrine.Tenancy;

/// <summary>
/// Pour toute route qui porte un <c>{slug}</c> (pages, images, webhook), fixe le restaurant courant de la requête ; un slug inconnu ou non publié
/// répond 404 avant d'atteindre l'endpoint. À placer après <c>UseRouting()</c> (les valeurs de route sont alors connues) et avant les endpoints.
/// </summary>
public sealed class SlugTenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SlugResolver resolver)
    {
        if (context.GetRouteValue("slug") is string slug)
        {
            if (await resolver.EnterAsync(slug, context.RequestAborted) is null)
            {
                // Les pages montrent la page « introuvable » ; les endpoints d'API (images, webhook) répondent un 404 nu.
                if (context.GetEndpoint()?.Metadata.GetMetadata<Endpoints.ApiEndpoint>() is not null)
                    context.Features.Get<IStatusCodePagesFeature>()?.Enabled = false;

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
        }

        await next(context);
    }
}
