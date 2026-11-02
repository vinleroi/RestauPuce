using ChefOS.Core.Common;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class SupplierServiceTests(PostgresFixture postgres)
{
    private const string UnknownSupplierMessage = "Fournisseur introuvable";

    private static SupplierService CreateService(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new(new TestContextFactory(options, restaurantId));

    [Fact]
    public async Task Save_CreatesThenUpdates()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var created = await service.SaveAsync(
            new SupplierInput(null, "  Metro  ", " Jean Dupont ", "jean@metro.test", "  "));

        Assert.True(created.Id > 0);
        Assert.Equal(restaurant, created.RestaurantId);
        Assert.Equal("Metro", created.Name);
        Assert.Equal("Jean Dupont", created.ContactName);
        Assert.Equal("jean@metro.test", created.Email);
        Assert.Null(created.Phone);

        var updated = await service.SaveAsync(
            new SupplierInput(created.Id, "Metro Cash & Carry", null, null, "+32 2 123 45 67"));

        Assert.Equal(created.Id, updated.Id);
        var stored = Assert.Single(await service.ListAsync());
        Assert.Equal("Metro Cash & Carry", stored.Name);
        Assert.Null(stored.ContactName);
        Assert.Null(stored.Email);
        Assert.Equal("+32 2 123 45 67", stored.Phone);
    }

    [Fact]
    public async Task List_OrdersByName_OnlyCurrentRestaurant()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var serviceA = CreateService(options, a);
        await serviceA.SaveAsync(new SupplierInput(null, "Metro", null, null, null));
        await serviceA.SaveAsync(new SupplierInput(null, "Boucherie Lambert", null, null, null));
        await CreateService(options, b).SaveAsync(new SupplierInput(null, "Delhaize", null, null, null));

        Assert.Equal(["Boucherie Lambert", "Metro"], (await serviceA.ListAsync()).Select(s => s.Name));
    }

    [Theory, InlineData(""), InlineData("   ")]
    public async Task Save_BlankName_Throws(string name)
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(new SupplierInput(null, name, null, null, null)));

        Assert.Equal("Le nom est obligatoire", ex.Message);
        Assert.Empty(await service.ListAsync());
    }

    [Fact]
    public async Task SaveAndDelete_UnknownOrForeignId_Throw()
    {
        var options = await postgres.CreateDatabaseAsync();
        var a = await TestData.CreateRestaurantAsync(options, "Restaurant A");
        var b = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var foreign = await CreateService(options, b).SaveAsync(new SupplierInput(null, "Delhaize", null, null, null));
        var service = CreateService(options, a);

        var save = await Assert.ThrowsAsync<DomainValidationException>(
            () => service.SaveAsync(new SupplierInput(foreign.Id, "Piraté", null, null, null)));
        var delete = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(foreign.Id));
        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => service.DeleteAsync(999_999));

        Assert.Equal(UnknownSupplierMessage, save.Message);
        Assert.Equal(UnknownSupplierMessage, delete.Message);
        Assert.Equal(UnknownSupplierMessage, unknown.Message);
        Assert.Equal("Delhaize", Assert.Single(await CreateService(options, b).ListAsync()).Name);
    }

    [Fact]
    public async Task Delete_RemovesSupplier()
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, "Bistro");
        var service = CreateService(options, restaurant);
        var metro = await service.SaveAsync(new SupplierInput(null, "Metro", null, null, null));
        await service.SaveAsync(new SupplierInput(null, "Delhaize", null, null, null));

        await service.DeleteAsync(metro.Id);

        Assert.Equal(["Delhaize"], (await service.ListAsync()).Select(s => s.Name));
    }
}
