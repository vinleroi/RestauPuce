using Anthropic;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using ChefOS.Infrastructure.Wines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Choix de l'assistant de rédaction des fiches de vin selon la configuration (section <c>Wines</c>, clé <c>Anthropic:ApiKey</c>). Aucun appel à l'API.</summary>
public sealed class WineDescriberRegistrationTests : IDisposable
{
    private const string ConnectionString = "Host=localhost;Port=1;Database=chefos_unused;Username=u;Password=p";
    private const string EnvironmentKey = "ANTHROPIC_API_KEY";

    private readonly string? _initialEnvironmentKey = Environment.GetEnvironmentVariable(EnvironmentKey);

    public WineDescriberRegistrationTests() => Environment.SetEnvironmentVariable(EnvironmentKey, null);

    public void Dispose() => Environment.SetEnvironmentVariable(EnvironmentKey, _initialEnvironmentKey);

    private static ServiceProvider Build(bool withReceipts, params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(null));
        services.AddChefOsInfrastructure(ConnectionString);
        if (withReceipts)
            services.AddChefOsReceiptReader(configuration);
        services.AddChefOsWineDescriber(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void Default_UsesTheDemoDescriber_AndWineService()
    {
        using var provider = Build(false);
        using var scope = provider.CreateScope();

        Assert.IsType<DemoWineDescriber>(scope.ServiceProvider.GetRequiredService<IWineDescriber>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<WineService>());
        Assert.Null(scope.ServiceProvider.GetService<AnthropicClient>());
    }

    [Fact]
    public void DemoMode_IgnoresAnyApiKey()
    {
        using var provider = Build(false, ("Wines:Describer", "Demo"), ("Anthropic:ApiKey", "sk-ant-test"));

        Assert.IsType<DemoWineDescriber>(provider.GetRequiredService<IWineDescriber>());
        Assert.Null(provider.GetService<AnthropicClient>());
    }

    [Theory]
    [InlineData("Claude")]
    [InlineData(" claude ")]
    public async Task ClaudeMode_WithoutKey_FailsWithAnExplicitMessage(string mode)
    {
        using var provider = Build(false, ("Wines:Describer", mode));

        var describer = provider.GetRequiredService<IWineDescriber>();

        Assert.IsNotType<ClaudeWineDescriber>(describer);
        Assert.IsNotType<DemoWineDescriber>(describer);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => describer.DescribeAsync(new WineDescribeRequest("Vin", null, null, null, [])));
        Assert.StartsWith("Clé API Anthropic non configurée", ex.Message);
    }

    [Fact]
    public void ClaudeMode_WithKey_UsesClaude()
    {
        using var provider = Build(false, ("Wines:Describer", "Claude"), ("Anthropic:ApiKey", "sk-ant-test"));

        Assert.IsType<ClaudeWineDescriber>(provider.GetRequiredService<IWineDescriber>());
        Assert.Equal("sk-ant-test", provider.GetRequiredService<AnthropicClient>().ApiKey);
    }

    [Fact]
    public void ClaudeMode_KeyInEnvironment_UsesClaude()
    {
        Environment.SetEnvironmentVariable(EnvironmentKey, "sk-ant-from-environment");

        using var provider = Build(false, ("Wines:Describer", "Claude"));

        Assert.IsType<ClaudeWineDescriber>(provider.GetRequiredService<IWineDescriber>());
        Assert.Equal("sk-ant-from-environment", provider.GetRequiredService<AnthropicClient>().ApiKey);
    }

    [Fact]
    public void ClaudeMode_SharesTheSingleClientWithTheReceiptReader()
    {
        using var provider = Build(true, ("Wines:Describer", "Claude"), ("Anthropic:ApiKey", "sk-ant-test"));

        var clients = provider.GetServices<AnthropicClient>().ToList();

        // Un seul enregistrement : celui des tickets, avec ses cinq minutes ; le describer en prend une copie à deux minutes.
        Assert.Single(clients);
        Assert.Equal(TimeSpan.FromMinutes(5), clients[0].Timeout);
        Assert.Equal(1, clients[0].MaxRetries);
        Assert.IsType<ClaudeWineDescriber>(provider.GetRequiredService<IWineDescriber>());
    }
}
