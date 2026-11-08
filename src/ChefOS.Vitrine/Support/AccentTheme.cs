using System.Globalization;
using System.Text.RegularExpressions;

namespace ChefOS.Vitrine.Support;

/// <summary>
/// Couleurs dérivées de la couleur d'accent du restaurant, calculées pour rester lisibles (contraste WCAG AA, 4,5:1) :
/// <see cref="Button"/> est le fond des boutons : l'accent tel quel quand un texte blanc ou sombre y est lisible, sinon l'accent assombri (texte blanc) ou éclairci
/// (texte sombre) jusqu'à 4,5:1 ; <see cref="Ink"/> est le texte posé dessus, <see cref="Deep"/> l'accent assombri au besoin pour servir de texte sur le fond clair du site.
/// Une valeur absente ou invalide (jamais injectée telle quelle dans le CSS) donne l'accent par défaut.
/// </summary>
public sealed partial record AccentTheme(string Accent, string Ink, string Deep, string Button)
{
    public const string DefaultAccent = "#8C3B2A";
    public const string Paper = "#FAF7F2";
    public const string Dark = "#1F1B16";

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z")]
    private static partial Regex HexPattern();

    public static AccentTheme From(string? hex)
    {
        var accent = hex is not null && HexPattern().IsMatch(hex) ? hex.ToUpperInvariant() : DefaultAccent;
        var rgb = Parse(accent);

        var white = Parse("#FFFFFF");
        var dark = Parse(Dark);
        var ink = Contrast(rgb, white) >= Contrast(rgb, dark) ? "#FFFFFF" : Dark;

        // Les couleurs moyennes (rouge vif, vert marin…) n'atteignent 4,5:1 ni avec du blanc ni avec du sombre : on déplace le fond du bouton.
        var button = rgb;
        var inkRgb = Parse(ink);
        for (var i = 0; i < 60 && Contrast(button, inkRgb) < 4.5; i++)
        {
            button = ink == "#FFFFFF"
                ? ((int)(button.R * 0.94), (int)(button.G * 0.94), (int)(button.B * 0.94))
                : (button.R + (int)((255 - button.R) * 0.08) + 1, button.G + (int)((255 - button.G) * 0.08) + 1, button.B + (int)((255 - button.B) * 0.08) + 1);
            button = (Math.Min(button.R, 255), Math.Min(button.G, 255), Math.Min(button.B, 255));
        }

        var paper = Parse(Paper);
        var deep = rgb;
        for (var i = 0; i < 40 && Contrast(deep, paper) < 4.5; i++)
            deep = ((int)(deep.R * 0.92), (int)(deep.G * 0.92), (int)(deep.B * 0.92));

        return new AccentTheme(accent, ink, Hex(deep), Hex(button));
    }

    /// <summary>Rapport de contraste WCAG entre deux couleurs « #RRGGBB ».</summary>
    public static double Contrast(string foreground, string background) => Contrast(Parse(foreground), Parse(background));

    /// <summary>Valeur de l'attribut <c>style</c> qui pose les variables CSS du restaurant.</summary>
    public string Style => $"--accent:{Accent};--accent-btn:{Button};--accent-ink:{Ink};--accent-deep:{Deep}";

    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static (int R, int G, int B) Parse(string hex) =>
        (int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static string Hex((int R, int G, int B) c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
