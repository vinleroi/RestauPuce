using ChefOS.Back.Components.Account;
using ChefOS.Back.Tenancy;
using ChefOS.Infrastructure;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ChefOS.Tests.Back;

/// <summary>Inscription contrôlée : ouverte en développement, pour le premier compte, ou si l'administrateur l'a ouverte.</summary>
[Collection("postgres")]
public sealed class RegistrationPolicyTests(PostgresFixture postgres)
{
    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "ChefOS.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private async Task<bool> IsOpenAsync(string environment, bool withUser, bool? allowRegistration = null)
    {
        var options = await postgres.CreateDatabaseAsync();
        string connectionString;
        using (var db = new ChefOsDbContext(options, new FixedTenantContext(null)))
            connectionString = db.Database.GetConnectionString()!;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new FixedTenantContext(null));
        services.AddChefOsInfrastructure(connectionString);
        services.AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<ChefOsDbContext>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (withUser)
            Assert.True((await users.CreateAsync(new ApplicationUser { UserName = "chef@chefos.test", Email = "chef@chefos.test" })).Succeeded);

        var values = new Dictionary<string, string?>();
        if (allowRegistration is { } allow)
            values["Accounts:AllowRegistration"] = allow.ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        return await new RegistrationPolicy(users, configuration, new Environment(environment)).IsOpenAsync();
    }

    [Fact]
    public async Task Development_WithoutSetting_IsOpen() =>
        Assert.True(await IsOpenAsync(Environments.Development, withUser: true));

    [Fact]
    public async Task Production_WithoutUser_IsOpen() =>
        Assert.True(await IsOpenAsync(Environments.Production, withUser: false));

    [Fact]
    public async Task Production_WithUser_IsClosed() =>
        Assert.False(await IsOpenAsync(Environments.Production, withUser: true));

    [Fact]
    public async Task Production_WithUser_AndAllowRegistration_IsOpen() =>
        Assert.True(await IsOpenAsync(Environments.Production, withUser: true, allowRegistration: true));
}
