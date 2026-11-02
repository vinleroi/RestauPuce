using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChefOS.Infrastructure.Data;

/// <summary>Utilisée par <c>dotnet ef</c> uniquement : base de dev locale, sans restaurant courant.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ChefOsDbContext>
{
    private const string DevConnectionString =
        "Host=localhost;Port=5433;Database=chefos;Username=chefos;Password=chefos_dev";

    public ChefOsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChefOsDbContext>()
            .UseNpgsql(DevConnectionString)
            .Options;

        return new ChefOsDbContext(options, new FixedTenantContext(null));
    }
}
