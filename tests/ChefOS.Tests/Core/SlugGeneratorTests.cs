using ChefOS.Core.Common;

namespace ChefOS.Tests.Core;

public class SlugGeneratorTests
{
    [Theory]
    [InlineData("Le Petit Bistro", "le-petit-bistro")]
    [InlineData("  Chez Hélène & Fils !! ", "chez-helene-fils")]
    [InlineData("L'Écailler du Palais", "l-ecailler-du-palais")]
    public void FromName(string name, string expected) => Assert.Equal(expected, SlugGenerator.FromName(name));
}
