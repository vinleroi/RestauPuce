using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ChefOS.Infrastructure.Notifications;

/// <summary>
/// Gabarit commun des e-mails : texte brut et HTML simple (une colonne, le titre en tête, la couleur d'accent, un bouton pour le lien).
/// Toute valeur passée est du texte : elle est échappée dans le HTML (jamais de balise venue d'une saisie). Un paragraphe peut tenir sur plusieurs lignes (« \n »).
/// </summary>
public static partial class EmailLayout
{
    /// <summary>Accent par défaut de la vitrine, pris quand celui du restaurant est absent ou n'est pas une couleur « #RRGGBB ».</summary>
    public const string DefaultAccent = "#8C3B2A";

    private const string Paper = "#FAF7F2";
    private const string Card = "#FFFFFF";
    private const string Dark = "#1F1B16";
    private const string Muted = "#6B635A";

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"[\p{Cc}\p{Zl}\p{Zp}]+")]
    private static partial Regex ControlPattern();

    [GeneratedRegex(@" {2,}")]
    private static partial Regex SpacesPattern();

    public static (string Text, string Html) Render(string title, string? accentColor, IReadOnlyList<string> paragraphs, (string Label, string Url)? button)
    {
        var heading = CleanSubject(title);
        var accent = accentColor is not null && HexPattern().IsMatch(accentColor) ? accentColor.ToUpperInvariant() : DefaultAccent;
        var (buttonBackground, buttonInk) = ButtonColors(accent);
        var titleColor = Darken(accent, Card); // l'accent tel quel quand il est lisible sur le blanc, assombri sinon

        var text = new StringBuilder();
        text.Append(heading).Append("\n\n");
        foreach (var paragraph in paragraphs)
            text.Append(paragraph.Trim()).Append("\n\n");
        if (button is { } link)
            text.Append(link.Label).Append(" : ").Append(link.Url).Append('\n');

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html lang=\"fr-BE\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(Escape(heading)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:").Append(Paper).Append(";\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:").Append(Paper).Append(";\">")
            .Append("<tr><td align=\"center\" style=\"padding:24px 12px;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:560px;background:").Append(Card)
            .Append(";border-top:4px solid ").Append(accent).Append(";border-radius:6px;\">")
            .Append("<tr><td style=\"padding:28px 28px 8px;font-family:Georgia,'Times New Roman',serif;font-size:24px;line-height:1.3;color:")
            .Append(titleColor).Append(";\">").Append(Escape(heading)).Append("</td></tr>")
            .Append("<tr><td style=\"padding:8px 28px 28px;font-family:Arial,Helvetica,sans-serif;font-size:16px;line-height:1.5;color:").Append(Dark).Append(";\">");
        foreach (var paragraph in paragraphs)
            html.Append("<p style=\"margin:0 0 16px;\">").Append(Lines(paragraph)).Append("</p>");
        if (button is { } action)
        {
            var href = Escape(action.Url);
            html.Append("<p style=\"margin:24px 0;\"><a href=\"").Append(href)
                .Append("\" style=\"display:inline-block;padding:12px 20px;background:").Append(buttonBackground).Append(";color:").Append(buttonInk)
                .Append(";text-decoration:none;border-radius:4px;font-weight:bold;\">").Append(Escape(action.Label)).Append("</a></p>")
                .Append("<p style=\"margin:0;font-size:13px;color:").Append(Muted).Append(";\">Si le bouton ne fonctionne pas, copiez ce lien dans votre navigateur :<br>")
                .Append("<a href=\"").Append(href).Append("\" style=\"color:").Append(Muted).Append(";word-break:break-all;\">").Append(href).Append("</a></p>");
        }
        html.Append("</td></tr></table></td></tr></table></body></html>");

        return (text.ToString(), html.ToString());
    }

    /// <summary>Le sujet sur une seule ligne : retours à la ligne, tabulations et autres caractères de contrôle deviennent une espace (pas d'en-tête injecté).</summary>
    public static string CleanSubject(string subject) =>
        SpacesPattern().Replace(ControlPattern().Replace(subject ?? string.Empty, " "), " ").Trim();

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);

    /// <summary>Un paragraphe échappé, ses lignes séparées par des <c>&lt;br&gt;</c>.</summary>
    private static string Lines(string paragraph) =>
        string.Join("<br>", paragraph.Trim().ReplaceLineEndings("\n").Split('\n').Select(line => Escape(line.Trim())));

    /// <summary>Fond et texte du bouton : l'accent avec le texte le plus lisible (blanc ou sombre), assombri au besoin jusqu'au contraste WCAG AA (4,5:1).</summary>
    private static (string Background, string Ink) ButtonColors(string accent)
    {
        if (Contrast(accent, Card) >= 4.5)
            return (accent, Card);
        if (Contrast(accent, Dark) >= 4.5)
            return (accent, Dark);
        return (Darken(accent, Card), Card);
    }

    /// <summary>L'accent assombri jusqu'à être lisible sur <paramref name="background"/> (4,5:1).</summary>
    private static string Darken(string accent, string background)
    {
        var (r, g, b) = Parse(accent);
        for (var i = 0; i < 40 && Contrast(Hex(r, g, b), background) < 4.5; i++)
            (r, g, b) = ((int)(r * 0.92), (int)(g * 0.92), (int)(b * 0.92));
        return Hex(r, g, b);
    }

    private static double Contrast(string foreground, string background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static double Luminance(string hex)
    {
        static double Channel(int v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        var (r, g, b) = Parse(hex);
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    private static (int R, int G, int B) Parse(string hex) =>
        (int.Parse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         int.Parse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
         int.Parse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static string Hex(int r, int g, int b) => $"#{r:X2}{g:X2}{b:X2}";
}
