using ChefOS.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ChefOS.Tests.Infrastructure;

[Collection(EnvironmentVariablesCollection.Name)]
public class ProductionConfigurationTests
{
    private static Dictionary<string, string?> Valid() => new()
    {
        ["ConnectionStrings:ChefOs"] = "Host=db;Database=chefos",
        ["Payments:Gateway"] = "Demo",
        ["Vitrine:BaseUrl"] = "https://vitrine.test",
        ["Email:Sender"] = "Log",
        // « Log » ne convient qu'à une démo hébergée : le compte de démonstration est déjà confirmé.
        ["Demo:SeedOnStartup"] = "true"
    };

    private static IReadOnlyList<string> Problems(Dictionary<string, string?> settings, string environment = "Production") =>
        ProductionConfiguration.Problems(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new Env(environment));

    [Fact]
    public void Development_HasNoProblems() =>
        Assert.Empty(Problems([], "Development"));

    [Fact]
    public void Production_MissingGateway_IsAProblem()
    {
        var settings = Valid();
        settings.Remove("Payments:Gateway");
        Assert.Contains(Problems(settings), p => p.Contains("Payments:Gateway"));
    }

    [Fact]
    public void Production_MollieWithoutKey_IsAProblem()
    {
        var previous = Environment.GetEnvironmentVariable("MOLLIE_API_KEY");
        Environment.SetEnvironmentVariable("MOLLIE_API_KEY", null);
        try
        {
            var settings = Valid();
            settings["Payments:Gateway"] = "mollie";
            Assert.Contains(Problems(settings), p => p.Contains("Mollie:ApiKey"));
            settings["Mollie:ApiKey"] = "test_secret";
            Assert.Empty(Problems(settings));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOLLIE_API_KEY", previous);
        }
    }

    [Fact]
    public void Production_MollieKeyOnlyInEnvironment_IsAccepted()
    {
        var previous = Environment.GetEnvironmentVariable("MOLLIE_API_KEY");
        Environment.SetEnvironmentVariable("MOLLIE_API_KEY", "test_secret_from_environment");
        try
        {
            var settings = Valid();
            settings["Payments:Gateway"] = "Mollie";
            Assert.Empty(Problems(settings));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MOLLIE_API_KEY", previous);
        }
    }

    [Fact]
    public void Production_LogWithConfirmationRequired_IsAProblem()
    {
        var settings = Valid();
        settings.Remove("Demo:SeedOnStartup");
        var problems = Problems(settings);
        Assert.Contains(problems, p => p.Contains("Email:Sender = Log") && p.Contains("Smtp"));

        settings["Accounts:RequireConfirmedEmail"] = "true";
        Assert.Contains(Problems(settings), p => p.Contains("Email:Sender = Log"));
        settings["Demo:SeedOnStartup"] = "false";
        Assert.Contains(Problems(settings), p => p.Contains("Email:Sender = Log"));
    }

    [Fact]
    public void Production_LogWithDemoSeed_IsAccepted()
    {
        var settings = Valid();
        settings["Demo:SeedOnStartup"] = "true";
        Assert.Empty(Problems(settings));
    }

    [Fact]
    public void Production_LogWithoutConfirmationRequired_IsAccepted()
    {
        var settings = Valid();
        settings.Remove("Demo:SeedOnStartup");
        settings["Accounts:RequireConfirmedEmail"] = "false";
        Assert.Empty(Problems(settings));
    }

    [Theory]
    [InlineData("pas une adresse")]
    [InlineData("no-reply@")]
    [InlineData("@chefos.test")]
    public void Production_SmtpWithInvalidFromAddress_IsAProblem(string from)
    {
        var settings = Valid();
        settings["Email:Sender"] = "Smtp";
        settings["Email:Smtp:Host"] = "smtp.test";
        settings["Email:FromAddress"] = from;
        Assert.Contains(Problems(settings), p => p.Contains("Email:FromAddress") && p.Contains("adresse e-mail valide"));
        settings["Email:FromAddress"] = "no-reply@chefos.test";
        Assert.Empty(Problems(settings));
    }

    [Fact]
    public void Production_HttpBaseUrl_IsAProblem()
    {
        var settings = Valid();
        settings["Vitrine:BaseUrl"] = "http://vitrine.test";
        Assert.Contains(Problems(settings), p => p.Contains("Vitrine:BaseUrl"));
        settings["Vitrine:BaseUrl"] = "pas une adresse";
        Assert.Contains(Problems(settings), p => p.Contains("Vitrine:BaseUrl"));
        settings.Remove("Vitrine:BaseUrl");
        Assert.Contains(Problems(settings), p => p.Contains("Vitrine:BaseUrl"));
    }

    [Fact]
    public void Production_SmtpWithoutHost_IsAProblem()
    {
        var settings = Valid();
        settings["Email:Sender"] = "Smtp";
        var problems = Problems(settings);
        Assert.Contains(problems, p => p.Contains("Email:Smtp:Host"));
        Assert.Contains(problems, p => p.Contains("Email:FromAddress"));
    }

    [Fact]
    public void Production_SmtpWithoutEncryption_IsAProblem()
    {
        var settings = Valid();
        settings["Email:Sender"] = "Smtp";
        settings["Email:Smtp:Host"] = "smtp.test";
        settings["Email:FromAddress"] = "no-reply@chefos.test";
        settings["Email:Smtp:Security"] = "None";
        Assert.Contains(Problems(settings), p => p.Contains("StartTls") && p.Contains("SslOnConnect"));
        settings["Email:Smtp:Security"] = "StartTls";
        Assert.Empty(Problems(settings));
        Assert.Empty(Problems(new Dictionary<string, string?>(settings) { ["Email:Smtp:Security"] = "None" }, "Development"));
    }

    [Fact]
    public void Production_ExplicitDemoAndLog_IsAccepted() =>
        Assert.Empty(Problems(Valid()));

    [Fact]
    public void Production_CompleteMollieSmtp_HasNoProblems()
    {
        var settings = Valid();
        settings["Payments:Gateway"] = "Mollie";
        settings["Mollie:ApiKey"] = "test_secret";
        settings["Email:Sender"] = "Smtp";
        settings["Email:Smtp:Host"] = "smtp.test";
        settings["Email:FromAddress"] = "no-reply@chefos.test";
        settings["Email:Smtp:Security"] = "SslOnConnect";
        Assert.Empty(Problems(settings));
    }

    [Fact]
    public void EnsureValid_ListsAllProblemsAndNoSecret()
    {
        var settings = new Dictionary<string, string?> { ["Mollie:ApiKey"] = "live_super_secret" };
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProductionConfiguration.EnsureValid(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), new Env("Production")));
        Assert.StartsWith("Configuration de production incomplète :", error.Message);
        Assert.Contains("ConnectionStrings:ChefOs", error.Message);
        Assert.Contains("Payments:Gateway", error.Message);
        Assert.Contains("Vitrine:BaseUrl", error.Message);
        Assert.Contains("Email:Sender", error.Message);
        Assert.DoesNotContain("live_super_secret", error.Message);
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
