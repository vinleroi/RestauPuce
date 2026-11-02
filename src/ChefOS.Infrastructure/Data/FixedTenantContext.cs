namespace ChefOS.Infrastructure.Data;

public sealed record FixedTenantContext(int? CurrentRestaurantId) : ITenantContext;
