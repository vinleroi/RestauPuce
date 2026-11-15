using Microsoft.AspNetCore.Diagnostics;

namespace ChefOS.Vitrine.Tenancy;

/// <summary>
/// Repère de la page du lien personnel d'une réservation (<c>/{slug}/reservation/{token}</c>) : pour elle seule, le restaurant est retrouvé par le jeton,
/// même si la vitrine n'est pas publiée (voir <see cref="SlugResolver.EnterReservationLinkAsync"/>). Lu dans les métadonnées de l'endpoint par
/// <see cref="SlugTenantMiddleware"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ReservationLinkPageAttribute : Attribute;

/// <summary>
/// Pour toute route qui porte un <c>{slug}</c> (pages, images, webhook), fixe le restaurant courant de la requête ; un slug inconnu ou non publié
/// répond 404 avant d'atteindre l'endpoint. À placer après <c>UseRouting()</c> (les valeurs de route sont alors connues) et avant les endpoints.
/// <para>
/// Exception : la page du lien d'une réservation (<see cref="ReservationLinkPageAttribute"/>). Le restaurant est celui du jeton, publié ou non ; un jeton
/// inconnu répond 404, et un lien dont le slug n'est pas le slug actuel de ce restaurant (slug changé, ou jeton d'un autre restaurant) redirige (302) vers le bon.
/// </para>
/// </summary>
public sealed class SlugTenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SlugResolver resolver)
    {
        if (context.GetRouteValue("slug") is string slug)
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<ReservationLinkPageAttribute>() is not null
                && context.GetRouteValue("token") is string token)
            {
                var restaurant = await resolver.EnterReservationLinkAsync(slug, token, context.RequestAborted);
                if (restaurant is null)
                {
                    NotFound(context);
                    return;
                }

                if (!SlugResolver.IsSameSlug(slug, restaurant.Slug))
                {
                    context.Response.Redirect($"{context.Request.PathBase}/{Uri.EscapeDataString(restaurant.Slug)}/reservation/{Uri.EscapeDataString(token)}");
                    return;
                }
            }
            else if (await resolver.EnterAsync(slug, context.RequestAborted) is null)
            {
                NotFound(context);
                return;
            }
        }

        await next(context);
    }

    private static void NotFound(HttpContext context)
    {
        // Les pages montrent la page « introuvable » ; les endpoints d'API (images, webhook) répondent un 404 nu.
        if (context.GetEndpoint()?.Metadata.GetMetadata<Endpoints.ApiEndpoint>() is not null)
            context.Features.Get<IStatusCodePagesFeature>()?.Enabled = false;

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }
}
