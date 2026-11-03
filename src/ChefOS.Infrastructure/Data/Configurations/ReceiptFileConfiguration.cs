using ChefOS.Core.Receipts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

internal sealed class ReceiptFileConfiguration : IEntityTypeConfiguration<ReceiptFile>
{
    public void Configure(EntityTypeBuilder<ReceiptFile> builder)
    {
        builder.BelongsToRestaurant();

        // Data (byte[]) devient un bytea : mapping par défaut.
        // Un ticket a au plus un fichier ; supprimer le ticket supprime son fichier.
        builder.HasOne<Receipt>()
            .WithOne()
            .HasForeignKey<ReceiptFile>(f => f.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
