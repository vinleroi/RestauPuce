using ChefOS.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ChefOS.Infrastructure.Data.Configurations;

/// <summary>
/// File d'e-mails : table globale (pas de filtre de restaurant, <see cref="OutgoingEmail"/> n'est pas <c>ITenantScoped</c>), exposée par aucune page.
/// L'index (statut, prochain essai) sert la réservation des messages dus.
/// </summary>
internal sealed class OutgoingEmailConfiguration : IEntityTypeConfiguration<OutgoingEmail>
{
    public void Configure(EntityTypeBuilder<OutgoingEmail> builder)
    {
        builder.ToTable("OutgoingEmails");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Kind).HasConversion<string>().HasMaxLength(40);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.To).HasMaxLength(EmailOutbox.AddressMaxLength);
        builder.Property(e => e.ReplyTo).HasMaxLength(EmailOutbox.AddressMaxLength);
        builder.Property(e => e.Subject).HasMaxLength(EmailOutbox.SubjectMaxLength);
        builder.Property(e => e.LastError).HasMaxLength(500);
        builder.HasIndex(e => new { e.Status, e.NextAttemptAt });
    }
}
