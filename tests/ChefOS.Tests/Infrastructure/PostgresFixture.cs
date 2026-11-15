using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// Un conteneur PostgreSQL par exécution de tests ; chaque test obtient sa propre base (nom unique, migrations appliquées, ou vide sur demande).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<DbContextOptions<ChefOsDbContext>> CreateDatabaseAsync()
    {
        var options = NewDatabaseOptions();

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        await db.Database.MigrateAsync();

        return options;
    }

    /// <summary>Une base créée mais vide : aucune migration, aucune table (premier démarrage d'un site sur un serveur neuf).</summary>
    public async Task<DbContextOptions<ChefOsDbContext>> CreateEmptyDatabaseAsync()
    {
        var options = NewDatabaseOptions();

        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        await db.GetService<IRelationalDatabaseCreator>().CreateAsync();

        return options;
    }

    /// <summary>La chaîne de connexion d'une base de test (pour la configuration d'un hôte).</summary>
    public static string ConnectionStringOf(DbContextOptions<ChefOsDbContext> options)
    {
        using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        return db.Database.GetConnectionString()!;
    }

    private DbContextOptions<ChefOsDbContext> NewDatabaseOptions()
    {
        // Pooling désactivé : une base par test × un pool par base saturerait max_connections du conteneur.
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = $"chefos_test_{Guid.NewGuid():N}",
            Pooling = false
        }.ConnectionString;

        return new DbContextOptionsBuilder<ChefOsDbContext>().UseNpgsql(connectionString).Options;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;
