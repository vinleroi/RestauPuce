using System.Text.RegularExpressions;

namespace ChefOS.Tests.Back;

/// <summary>
/// Le back, les services et le seed ne lisent jamais l'horloge du serveur : l'heure vient du <see cref="TimeProvider"/> et les jours,
/// du fuseau du restaurant (<c>RestaurantCalendar</c>). Un serveur en UTC ne doit pas décaler « aujourd'hui » ni les heures affichées.
/// </summary>
public class NoServerClockTests
{
    private static readonly Regex ServerClock = new(
        @"DateTime\.UtcNow|DateTime\.Now|DateTime\.Today|DateTimeOffset\.Now|DateTimeOffset\.UtcNow|\.ToLocalTime\(\)", RegexOptions.Compiled);

    [Fact]
    public void SourcesNeverReadTheServerClock()
    {
        var root = FindSolutionRoot();
        string[] folders =
        [
            Path.Combine("src", "ChefOS.Back"),
            Path.Combine("src", "ChefOS.Infrastructure", "Services"),
            Path.Combine("src", "ChefOS.Infrastructure", "Seeding"),
            Path.Combine("src", "ChefOS.Infrastructure", "Receipts")
        ];

        var offenders = new List<string>();
        foreach (var folder in folders)
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, folder), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs") || f.EndsWith(".razor")))
        {
            var relative = Path.GetRelativePath(root, file);
            var parts = relative.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => p is "bin" or "obj" or "Migrations"))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (ServerClock.IsMatch(lines[i]))
                    offenders.Add($"{relative}:{i + 1}");
        }

        Assert.True(offenders.Count == 0, "Horloge du serveur lue dans :\n" + string.Join("\n", offenders));
    }

    private static string FindSolutionRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (dir.EnumerateFiles().Any(f => string.Equals(f.Name, "ChefOS.sln", StringComparison.OrdinalIgnoreCase)))
                return dir.FullName;

        throw new InvalidOperationException("ChefOS.sln introuvable en remontant depuis " + AppContext.BaseDirectory);
    }
}
