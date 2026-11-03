using ChefOS.Core.Receipts;

namespace ChefOS.Tests.Core;

public class LabelNormalizerTests
{
    [Theory]
    [InlineData("Filet poul. 2,35KG", "FILET POUL 2 35KG")]
    [InlineData("  Crème fraîche 40% ", "CREME FRAICHE 40")]
    [InlineData("Bœuf haché", "BOEUF HACHE")]
    [InlineData("Œufs   frais\t(x30)", "OEUFS FRAIS X30")]
    [InlineData("---", "")]
    [InlineData("", "")]
    public void Normalize(string raw, string expected) => Assert.Equal(expected, LabelNormalizer.Normalize(raw));
}
