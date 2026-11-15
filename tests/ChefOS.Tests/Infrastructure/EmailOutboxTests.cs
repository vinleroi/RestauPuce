using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// <see cref="EmailOutbox.Enqueue"/> s'exécute dans la transaction de l'événement (réservation, paiement) : il ne doit jamais la faire échouer,
/// ni par une longueur de colonne dépassée, ni par une date qui n'est pas en UTC.
/// </summary>
[Collection("postgres")]
public sealed class EmailOutboxTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Enqueue_RespectsTheColumnLimits_AndStoresUtc_WithoutFailingTheTransaction()
    {
        var options = await postgres.CreateDatabaseAsync();
        var longAddress = new string('a', 330) + "@resto.test";
        var utc = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        // Un sujet de 299 caractères suivi d'un émoji : la coupe à 300 ne doit pas laisser une moitié de paire de substitution.
        var subject = new string('s', 299) + "🍷 et la suite";

        bool tooLongTo, kept;
        await using (var db = new ChefOsDbContext(options, new FixedTenantContext(null)))
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            kept = EmailOutbox.Enqueue(db, new EmailMessage(EmailKind.ReservationConfirmed, " client@resto.test ", subject, "Texte", "",
                ReplyTo: longAddress, RestaurantId: 7), utc.ToLocalTime());
            tooLongTo = EmailOutbox.Enqueue(db, new EmailMessage(EmailKind.ReservationConfirmed, longAddress, "Sujet", "Texte", ""), utc);
            EmailOutbox.Enqueue(db, new EmailMessage(EmailKind.PasswordReset, "autre@resto.test", "Ligne\r\nBcc: x@y", "Texte", "<p>x</p>"),
                DateTime.SpecifyKind(utc, DateTimeKind.Unspecified));
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Assert.True(kept);
        Assert.False(tooLongTo); // une adresse tronquée serait une autre adresse : le message n'est pas mis en file
        await using var read = new ChefOsDbContext(options, new FixedTenantContext(null));
        var rows = await read.OutgoingEmails.AsNoTracking().OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("client@resto.test", rows[0].To);
        Assert.Null(rows[0].ReplyTo); // trop longue : ignorée plutôt que tronquée en adresse invalide
        Assert.Equal(new string('s', 299), rows[0].Subject);
        Assert.Equal((utc, utc), (rows[0].CreatedAt, rows[0].NextAttemptAt));
        Assert.Equal(DateTimeKind.Utc, rows[0].CreatedAt.Kind);
        Assert.Equal("Ligne Bcc: x@y", rows[1].Subject);
        Assert.Equal(utc, rows[1].CreatedAt);
        Assert.Null(rows[0].HtmlBody);
        Assert.Equal("<p>x</p>", rows[1].HtmlBody);
    }
}
