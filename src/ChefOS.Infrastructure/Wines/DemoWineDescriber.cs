using System.Text.RegularExpressions;
using ChefOS.Core.Vitrine;

namespace ChefOS.Infrastructure.Wines;

/// <summary>
/// Describer de démonstration : ni clé ni réseau. Devine la couleur d'après le nom et renvoie un texte générique de cette couleur,
/// sans rien inventer sur le vin (ni producteur, ni région) ; propose les deux premiers plats de la liste.
/// </summary>
public sealed class DemoWineDescriber : IWineDescriber
{
    public Task<WineDraft> DescribeAsync(WineDescribeRequest request, CancellationToken ct = default)
    {
        var color = GuessColor($"{request.Name} {request.Appellation}");
        var (robe, nez, bouche, garde, temperature, pairings) = Texts(color);

        return Task.FromResult(new WineDraft(
            string.IsNullOrWhiteSpace(request.Appellation) ? null : request.Appellation.Trim(),
            "", "", color, "", robe, nez, bouche, garde, temperature, pairings,
            request.DishNames.Take(2).ToList()));
    }

    private static WineColor GuessColor(string text)
    {
        var lower = text.ToLowerInvariant();
        if (ContainsAny(lower, "champagne", "crémant", "cremant", "cava", "prosecco", "brut", "mousseux", "pétillant"))
            return WineColor.Effervescent;
        if (ContainsAny(lower, "sauternes", "moelleux", "liquoreux", "muscat", "porto", "late harvest", "vendanges tardives"))
            return WineColor.Doux;
        if (Regex.IsMatch(lower, @"\b(rosé|rose|rosado)\b"))
            return WineColor.Rose;
        if (ContainsAny(lower, "blanc", "chardonnay", "sauvignon", "riesling", "chablis", "muscadet", "viognier", "chenin", "albariño"))
            return WineColor.Blanc;

        return WineColor.Rouge;
    }

    private static bool ContainsAny(string text, params string[] words) => words.Any(text.Contains);

    private static (string Robe, string Nez, string Bouche, string Garde, string Temperature, string Pairings) Texts(WineColor color) => color switch
    {
        WineColor.Blanc => (
            "Une robe claire aux reflets dorés. Elle annonce un vin net et lumineux.",
            "Un nez frais de fruits blancs et d'agrumes. Quelques notes florales peuvent s'y ajouter.",
            "Une bouche vive et équilibrée, portée par une belle fraîcheur. La finale est nette.",
            "À boire dans les premières années pour profiter de sa fraîcheur.",
            "8-10 °C",
            "Poissons, crustacés et volailles. Il accompagne aussi les fromages de chèvre."),
        WineColor.Rose => (
            "Une robe rose pâle aux reflets saumonés. Elle évoque un vin léger et gourmand.",
            "Un nez de fruits rouges croquants et de pamplemousse. L'ensemble reste délicat.",
            "Une bouche fraîche et souple, avec une finale désaltérante.",
            "À boire jeune, dans l'année ou les deux ans qui suivent.",
            "8-10 °C",
            "Cuisine d'été, charcuteries et poissons grillés. Il se plaît aussi à l'apéritif."),
        WineColor.Effervescent => (
            "Une robe claire animée d'un cordon de fines bulles. Elle annonce un vin festif.",
            "Un nez de fruits blancs, de brioche et de fleurs blanches. Il reste frais et élégant.",
            "Une bouche vive aux bulles fines, avec une belle tension en finale.",
            "À boire dans les premières années, bien frais.",
            "6-8 °C",
            "À l'apéritif, avec les fruits de mer ou les fritures légères."),
        WineColor.Doux => (
            "Une robe dorée et brillante. Elle laisse deviner un vin riche.",
            "Un nez de fruits mûrs, de miel et de fruits confits. Il reste gourmand.",
            "Une bouche ample et suave, soutenue par une fraîcheur qui évite la lourdeur.",
            "Il se garde longtemps et peut gagner en complexité avec les années.",
            "8-10 °C",
            "Foie gras, fromages bleus et desserts aux fruits. Il peut aussi se boire seul."),
        _ => (
            "Une robe rouge aux reflets rubis. Elle annonce un vin de belle intensité.",
            "Un nez de fruits rouges et noirs mûrs. Quelques notes d'épices peuvent s'y ajouter.",
            "Une bouche équilibrée aux tanins soyeux. La finale est persistante.",
            "À boire dès maintenant ou à garder quelques années.",
            "16-18 °C",
            "Viandes rouges, volailles rôties et fromages affinés.")
    };
}
