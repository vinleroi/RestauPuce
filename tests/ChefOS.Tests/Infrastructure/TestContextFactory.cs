using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Fabrique de contextes liés à un restaurant donné (ou sans restaurant si <c>null</c>).</summary>
public sealed class TestContextFactory(DbContextOptions<ChefOsDbContext> options, int? restaurantId)
    : IDbContextFactory<ChefOsDbContext>
{
    public ChefOsDbContext CreateDbContext() => new(options, new FixedTenantContext(restaurantId));
}
