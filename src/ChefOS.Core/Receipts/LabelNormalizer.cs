using System.Globalization;
using System.Text;

namespace ChefOS.Core.Receipts;

public static class LabelNormalizer
{
    /// <summary>
    /// Forme comparable d'un libellé de ticket : majuscules, sans diacritiques, tout ce qui n'est pas [A-Z0-9] devient un séparateur,
    /// séparateurs fusionnés en une espace et rognés (« Crème fraîche 40% » → « CREME FRAICHE 40 »).
    /// </summary>
    public static string Normalize(string label)
    {
        // Ligatures sans décomposition Unicode : écrites en toutes lettres (« Bœuf » → « BOEUF »), comme pour les slugs.
        var expanded = label
            .Replace("œ", "oe").Replace("Œ", "oe")
            .Replace("æ", "ae").Replace("Æ", "ae")
            .Replace("ß", "ss");

        var decomposed = expanded.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        var separatorPending = false;

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            var upper = char.ToUpperInvariant(c);
            if (upper is (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                if (separatorPending && result.Length > 0)
                    result.Append(' ');
                result.Append(upper);
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }

        return result.ToString();
    }
}
