using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// Un conteneur PostgreSQL par exécution de tests ; chaque test obtient sa propre base (nom unique, migrations appliquées).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<DbContextOptions<ChefOsDbContext>> CreateDatabaseAsync()
    {
        // Pooling désactivé : une base par test × un pool par base saturerait max_connections du conteneur.
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = $"chefos_test_{Guid.NewGuid():N}",
            Pooling = false
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<ChefOsDbContext>().UseNpgsql(connectionString).Options;

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        await db.Database.MigrateAsync();

        return options;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
