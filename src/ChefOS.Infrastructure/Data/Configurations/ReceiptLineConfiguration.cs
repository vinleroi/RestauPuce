using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class ReceiptLineConfiguration : IEntityTypeConfiguration<ReceiptLine>
{
    public void Configure(EntityTypeBuilder<ReceiptLine> builder)
    {
        builder.BelongsToRestaurant();

        builder.Property(l => l.Unit).HasConversion<string>();
        builder.Property(l => l.MatchSource).HasConversion<string>();
        builder.Property(l => l.Quantity).HasPrecision(18, 4);
        builder.Property(l => l.UnitPrice).HasPrecision(18, 2);
        builder.Property(l => l.LineTotal).HasPrecision(18, 2);
        builder.Property(l => l.VatRate).HasPrecision(5, 4);
        builder.Property(l => l.LineTotalExclVat).HasPrecision(18, 2);
        builder.Property(l => l.AppliedPricePerBaseUnit).HasPrecision(18, 6);

        // La relation vers le ticket (cascade) est déclarée côté Receipt.Lines.
        // Supprimer l'ingrédient garde la ligne du ticket, qui redevient « à rapprocher ».
        builder.HasOne<Ingredient>()
            .WithMany()
            .HasForeignKey(l => l.IngredientId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
