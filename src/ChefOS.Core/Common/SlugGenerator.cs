using System.Globalization;
using System.Text;

namespace ChefOS.Core.Common;

public static class SlugGenerator
{
    public static string FromName(string name)
    {
        // Ligatures sans décomposition Unicode : écrites en toutes lettres pour garder un slug lisible (« Bœuf » → « boeuf »).
        var expanded = name
            .Replace("œ", "oe").Replace("Œ", "oe")
            .Replace("æ", "ae").Replace("Æ", "ae")
            .Replace("ß", "ss");

        var decomposed = expanded.Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder(decomposed.Length);
        var separatorPending = false;

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            var lower = char.ToLowerInvariant(c);
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                if (separatorPending && slug.Length > 0)
                    slug.Append('-');
                slug.Append(lower);
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }

        return slug.ToString();
    }
}
