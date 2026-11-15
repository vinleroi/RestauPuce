using ChefOS.Infrastructure;
using ChefOS.Vitrine.Support;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ChefOS.Tests.Vitrine;

public class AccentThemeTests
{
    [Theory]
    [InlineData("#8C3B2A")]
    [InlineData("#F4D35E")]
    [InlineData("#1E3A8A")]
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    [InlineData("#E8A3A8")]
    [InlineData("#FF0000")]
    [InlineData("#2E8B57")]
    [InlineData("#808080")]
    [InlineData("#00A0A0")]
    [InlineData("#FF7F50")]
    public void Colors_AlwaysReachWcagAA(string accent)
    {
        var theme = AccentTheme.From(accent);

        Assert.True(AccentTheme.Contrast(theme.Ink, theme.Button) >= 4.5, "texte sur le fond du bouton");
        Assert.True(AccentTheme.Contrast(theme.Deep, AccentTheme.Paper) >= 4.5, "accent en texte sur le fond clair");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("#ff0000;background:url(x)")]
    public void InvalidColor_FallsBackToTheDefault_AndNeverReachesTheStyle(string? bad)
    {
        var theme = AccentTheme.From(bad);

        Assert.Equal(AccentTheme.DefaultAccent, theme.Accent);
        Assert.DoesNotContain("url", theme.Style);
    }

    [Fact]
    public void ReadableAccent_IsUsedAsIsForButtons_WhileAMidToneIsMovedUntilReadable()
    {
        Assert.Equal("#8C3B2A", AccentTheme.From("#8C3B2A").Button);
        Assert.NotEqual("#FF0000", AccentTheme.From("#FF0000").Button);
        Assert.Contains("--accent-btn:", AccentTheme.From("#FF0000").Style);
    }

    [Fact]
    public void ValidColor_IsKept_Uppercased()
    {
        var theme = AccentTheme.From("#2e7d32");

        Assert.Equal("#2E7D32", theme.Accent);
        Assert.Contains("--accent:#2E7D32", theme.Style);
    }
}

public class SeoTests
{
    [Fact]
    public void Title_IsNameDashTagline_OrNameAlone()
    {
        Assert.Equal("Chez Anne — Cuisine de saison", Seo.Title("Chez Anne", " Cuisine de saison "));
        Assert.Equal("Chez Anne", Seo.Title("Chez Anne", null));
        Assert.Equal("Chez Anne", Seo.Title("Chez Anne", "  "));
    }

    [Fact]
    public void Description_PrefersTagline_ThenAmbianceExcerpt_ThenNameAndCity()
    {
        Assert.Equal("Cuisine de saison", Seo.Description("Chez Anne", "Liège", "Cuisine de saison", "Long texte"));
        Assert.Equal("Une salle chaleureuse", Seo.Description("Chez Anne", "Liège", null, "Une  salle\nchaleureuse"));
        Assert.Equal("Chez Anne à Liège", Seo.Description("Chez Anne", "Liège", null, null));
        Assert.Equal("Chez Anne", Seo.Description("Chez Anne", null, " ", " "));
    }

    [Fact]
    public void LongAmbiance_IsCutAtAWord_Within155Characters()
    {
        var text = string.Join(' ', Enumerable.Repeat("accueillante", 30));

        var excerpt = Seo.Description("N", null, null, text);

        Assert.True(excerpt.Length <= 156);
        Assert.EndsWith("…", excerpt);
        Assert.DoesNotContain("accueillant…", excerpt.Replace("accueillante…", ""));
    }
}

public class PublicUrlsTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static PublicUrls Create(string environment) =>
        new(Options.Create(new PublicSiteOptions { BaseUrl = "https://vitrine.example/" }), new Env(environment));

    [Fact]
    public void ReturnBase_IsUnderTheRestaurantSlug()
    {
        Assert.Equal("https://vitrine.example/le-bistro/reservation", Create("Production").ReservationBase("le-bistro"));
    }

    [Fact]
    public void Webhook_IsPublic_ExceptInDevelopmentWhereMollieCannotReachLocalhost()
    {
        Assert.Equal("https://vitrine.example/le-bistro/webhooks/mollie", Create("Production").Webhook("le-bistro"));
        Assert.Equal("", Create("Development").Webhook("le-bistro"));
    }
}

public class AttemptLimiterTests
{
    [Fact]
    public void TenAttemptsPerKeyPerMinute_ThenRefused_WhileOtherKeysAreUnaffected()
    {
        using var limiter = new AttemptLimiter();

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.TryAcquire("ip:1.2.3.4"));

        Assert.False(limiter.TryAcquire("ip:1.2.3.4"));
        Assert.True(limiter.TryAcquire("ip:5.6.7.8"));
    }

    [Fact]
    public void CircuitAttempts_AreLimitedPerCircuit_AndPerClientAddress()
    {
        using var limiter = new AttemptLimiter();
        var first = new CircuitAttempts(limiter);

        for (var i = 0; i < 10; i++)
            Assert.True(first.TryAcquire("9.9.9.9"));
        Assert.False(first.TryAcquire("9.9.9.9"));

        // Un autre circuit du même client partage la limite de l'adresse IP.
        Assert.False(new CircuitAttempts(limiter).TryAcquire("9.9.9.9"));
        // Un autre client n'est pas touché.
        Assert.True(new CircuitAttempts(limiter).TryAcquire("8.8.8.8"));
    }
}
