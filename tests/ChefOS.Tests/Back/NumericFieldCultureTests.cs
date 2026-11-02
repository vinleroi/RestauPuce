using System.Text.RegularExpressions;

namespace ChefOS.Tests.Back;

/// <summary>
/// MudNumericField prend la culture invariante par défaut : la virgule française y est refusée. Chaque champ numérique du back
/// doit donc recevoir <c>Culture="Ui.Fr"</c> ; ce test échoue dès qu'un champ est ajouté sans culture.
/// </summary>
public class NumericFieldCultureTests
{
    [Fact]
    public void EveryNumericFieldOfTheBack_DeclaresACulture()
    {
        var offenders = NumericFieldTags()
            .Where(tag => !tag.Tag.Contains("Culture=", StringComparison.Ordinal))
            .Select(tag => $"{tag.File} : {Summary(tag.Tag)}")
            .ToList();

        Assert.True(offenders.Count == 0, "MudNumericField sans Culture=\"Ui.Fr\" :" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheScan_FindsTheNumericFieldsOfTheBack()
    {
        // Garde-fou : un chemin faux ferait passer le test précédent sans rien vérifier.
        Assert.NotEmpty(NumericFieldTags());
    }

    private static List<(string File, string Tag)> NumericFieldTags()
    {
        var backDirectory = Path.Combine(RepositoryRoot(), "src", "ChefOS.Back");
        var tags = new List<(string, string)>();

        foreach (var file in Directory.EnumerateFiles(backDirectory, "*.razor", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var content = File.ReadAllText(file);
            foreach (Match start in Regex.Matches(content, @"<MudNumericField\b"))
            {
                // Les valeurs d'attributs peuvent contenir « > » (lambdas) : on lit jusqu'à la fin de balise « /> » ou, à défaut, « > ».
                var end = content.IndexOf("/>", start.Index, StringComparison.Ordinal);
                if (end < 0)
                    end = content.IndexOf('>', start.Index);
                tags.Add((Path.GetRelativePath(backDirectory, file), content[start.Index..(end < 0 ? content.Length : end + 2)]));
            }
        }

        return tags;
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChefOS.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("ChefOS.sln est introuvable au-dessus de " + AppContext.BaseDirectory);
    }

    private static string Summary(string tag) => Regex.Replace(tag, @"\s+", " ");
}
