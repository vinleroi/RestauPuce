using Anthropic;
using ChefOS.Core.Receipts;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Receipts;
using ChefOS.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// Choix du lecteur de tickets selon la configuration (section <c>Receipts</c>, clé <c>Anthropic:ApiKey</c>, variable <c>ANTHROPIC_API_KEY</c>).
/// Seuls les enregistrements sont exercés : aucun appel à l'API. La variable d'environnement est réinitialisée pour chaque test
/// (une clé réelle présente sur le poste du développeur ne doit pas fausser le résultat) ; la classe est séquentielle, les tests ne se chevauchent pas.
/// </summary>
public sealed class ReceiptReaderRegistrationTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=1;Database=chefos_unused;Username=u;Password=p";
    private const string EnvironmentKey = "ANTHROPIC_API_KEY";

    private readonly string? _initialEnvironmentKey = Environment.GetEnvironmentVariable(EnvironmentKey);

    public ReceiptReaderRegistrationTests() => Environment.SetEnvironmentVariable(EnvironmentKey, null);

    public void Dispose() => Environment.SetEnvironmentVariable(EnvironmentKey, _initialEnvironmentKey);

    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(null));
        services.AddChefOsInfrastructure(ConnectionString);
        services.AddChefOsReceiptReader(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Theory]
    [InlineData("Demo")]
    [InlineData("demo")]
    [InlineData(" DEMO ")]
    public void Registration_DemoMode_UsesDemoReader(string reader)
    {
        using var provider = Build(("Receipts:Reader", reader));
        using var scope = provider.CreateScope();

        Assert.IsType<DemoReceiptReader>(scope.ServiceProvider.GetRequiredService<IReceiptReader>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReceiptService>());
        Assert.Null(scope.ServiceProvider.GetService<AnthropicClient>());
    }

    [Fact]
    public void Registration_DemoMode_IgnoresAnyApiKey()
    {
        using var provider = Build(("Receipts:Reader", "Demo"), ("Anthropic:ApiKey", "sk-ant-test"));
        using var scope = provider.CreateScope();

        Assert.IsType<DemoReceiptReader>(scope.ServiceProvider.GetRequiredService<IReceiptReader>());
    }

    [Fact]
    public async Task Registration_NoKey_UsesMissingKeyReader()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        var reader = scope.ServiceProvider.GetRequiredService<IReceiptReader>();
        Assert.IsNotType<ClaudeReceiptReader>(reader);
        Assert.IsNotType<DemoReceiptReader>(reader);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReceiptService>());
        Assert.Null(scope.ServiceProvider.GetService<AnthropicClient>());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync([1], "image/png"));
        Assert.Equal(
            "Clé API Anthropic non configurée : voir le README (dotnet user-secrets set Anthropic:ApiKey …)",
            ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Registration_BlankKey_UsesMissingKeyReader(string key)
    {
        using var provider = Build(("Anthropic:ApiKey", key));
        using var scope = provider.CreateScope();

        var reader = scope.ServiceProvider.GetRequiredService<IReceiptReader>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync([1], "image/png"));
    }

    [Fact]
    public void Registration_KeyInConfiguration_UsesClaudeReader()
    {
        using var provider = Build(("Anthropic:ApiKey", "sk-ant-from-configuration"));
        using var scope = provider.CreateScope();

        Assert.IsType<ClaudeReceiptReader>(scope.ServiceProvider.GetRequiredService<IReceiptReader>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ReceiptService>());
        Assert.Equal("sk-ant-from-configuration", provider.GetRequiredService<AnthropicClient>().ApiKey);
    }

    [Fact]
    public void Registration_ClaudeClient_RetriesOnceAndKeepsTheSdkTimeout()
    {
        using var provider = Build(("Anthropic:ApiKey", "sk-ant-test"));

        var client = provider.GetRequiredService<AnthropicClient>();

        // Chaque nouvel essai est facturé et le SDK relance aussi après un délai dépassé : un seul. Le délai (480 s pour 16 000 jetons) reste celui du SDK.
        Assert.Equal(1, client.MaxRetries);
        Assert.Null(client.Timeout);
    }

    [Fact]
    public void Registration_KeyInEnvironment_UsesClaudeReader()
    {
        Environment.SetEnvironmentVariable(EnvironmentKey, "sk-ant-from-environment");

        using var provider = Build();
        using var scope = provider.CreateScope();

        Assert.IsType<ClaudeReceiptReader>(scope.ServiceProvider.GetRequiredService<IReceiptReader>());
        Assert.Equal("sk-ant-from-environment", provider.GetRequiredService<AnthropicClient>().ApiKey);
    }

    [Fact]
    public void Registration_KeyInConfiguration_WinsOverEnvironment()
    {
        Environment.SetEnvironmentVariable(EnvironmentKey, "sk-ant-from-environment");

        using var provider = Build(("Anthropic:ApiKey", "sk-ant-from-configuration"));

        Assert.Equal("sk-ant-from-configuration", provider.GetRequiredService<AnthropicClient>().ApiKey);
    }

    [Fact]
    public void Registration_ExplicitClaudeMode_UsesClaudeReader()
    {
        using var provider = Build(("Receipts:Reader", "Claude"), ("Anthropic:ApiKey", "sk-ant-test"));
        using var scope = provider.CreateScope();

        Assert.IsType<ClaudeReceiptReader>(scope.ServiceProvider.GetRequiredService<IReceiptReader>());
    }

    [Fact]
    public void Registration_BindsReceiptReaderOptions_WithClaudeAsDefault()
    {
        using var defaults = Build();
        Assert.Equal("Claude", defaults.GetRequiredService<IOptions<ReceiptReaderOptions>>().Value.Reader);

        using var demo = Build(("Receipts:Reader", "Demo"));
        Assert.Equal("Demo", demo.GetRequiredService<IOptions<ReceiptReaderOptions>>().Value.Reader);
    }
}
