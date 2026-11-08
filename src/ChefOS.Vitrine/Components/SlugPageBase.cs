using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Services;
using ChefOS.Vitrine.Tenancy;
using Microsoft.AspNetCore.Components;

namespace ChefOS.Vitrine.Components;

/// <summary>
/// Base des pages d'un restaurant (<c>/{slug}/…</c>). À chaque changement de slug elle fixe le restaurant courant du scope (requête ou circuit interactif :
/// le middleware ne couvre que les requêtes HTTP), charge le contenu public et répond 404 si le lien est inconnu ou la vitrine non publiée.
/// </summary>
public abstract class SlugPageBase : ComponentBase
{
    [Parameter] public string Slug { get; set; } = "";

    [Inject] protected SlugResolver Resolver { get; set; } = default!;
    [Inject] protected VitrineService VitrineService { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected RestaurantRef? Restaurant { get; private set; }
    protected PublicVitrine? Vitrine { get; private set; }
    protected VitrineProfile? Profile { get; private set; }

    /// <summary>Faux tant que le contenu n'est pas chargé (ou si le lien est inconnu, auquel cas la réponse est un 404).</summary>
    protected bool Ready => Restaurant is not null && Vitrine is not null;

    protected override async Task OnParametersSetAsync()
    {
        Restaurant = await Resolver.EnterAsync(Slug);
        Vitrine = Restaurant is null ? null : await VitrineService.GetPublicAsync();
        if (Vitrine is null)
        {
            Restaurant = null;
            Nav.NotFound();
            return;
        }

        Profile = await VitrineService.FindProfileAsync();
        await LoadAsync();
    }

    /// <summary>Chargement propre à la page, une fois le restaurant fixé et la vitrine publiée.</summary>
    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected string Url(string path = "") => $"/{Uri.EscapeDataString(Slug)}{path}";
}
