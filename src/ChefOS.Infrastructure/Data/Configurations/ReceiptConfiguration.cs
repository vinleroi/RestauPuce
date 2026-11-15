using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    public void Configure(EntityTypeBuilder<Receipt> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(r => r.Status).HasConversion<string>();
        builder.Property(r => r.Total).HasPrecision(18, 2);

        // Jeton de version : colonne système xmin (aucune colonne ajoutée).
        builder.Property(r => r.Version).IsRowVersion();

        // Un ticket survit à la suppression de son fournisseur : le libellé lu (SupplierNameRaw) reste.
        builder.HasOne<Supplier>()
            .WithMany()
            .HasForeignKey(r => r.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);

        // Les lignes sont triées par Position côté requêtes, pas dans le modèle.
        builder.HasMany(r => r.Lines)
            .WithOne()
            .HasForeignKey(l => l.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
