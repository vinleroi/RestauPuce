using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>Fournisseurs du restaurant courant.</summary>
public class SupplierService(IDbContextFactory<ChefOsDbContext> factory)
{
    private const string UnknownSupplierMessage = "Fournisseur introuvable";

    public async Task<IReadOnlyList<Supplier>> ListAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.Suppliers.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
    }

    /// <summary>Crée (<c>Id</c> null) ou met à jour un fournisseur du restaurant courant.</summary>
    public async Task<Supplier> SaveAsync(SupplierInput input)
    {
        var name = RequireName(input.Name);

        await using var db = await factory.CreateDbContextAsync();

        var supplier = input.Id is { } id
            ? await RequireSupplierAsync(db, id)
            : db.Suppliers.Add(new Supplier()).Entity;

        supplier.Name = name;
        supplier.ContactName = NullIfBlank(input.ContactName);
        supplier.Email = NullIfBlank(input.Email);
        supplier.Phone = NullIfBlank(input.Phone);

        await db.SaveChangesAsync();
        return supplier;
    }

    /// <summary>Les ingrédients et prix liés perdent leur fournisseur (<c>SupplierId</c> à null) via la clé étrangère SetNull.</summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        db.Suppliers.Remove(await RequireSupplierAsync(db, id));
        await db.SaveChangesAsync();
    }

    private static async Task<Supplier> RequireSupplierAsync(ChefOsDbContext db, int id) =>
        await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id)
        ?? throw new DomainValidationException(UnknownSupplierMessage);

    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainValidationException("Le nom est obligatoire");

        return name.Trim();
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
