using ChefOS.Back.Components.Account;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Notifications;
using ChefOS.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace ChefOS.Tests.Back;

/// <summary>Les e-mails d'Identity (confirmation, mot de passe oublié) entrent dans la file globale, en français, avec le lien intact.</summary>
[Collection("postgres")]
public sealed class IdentityOutboxEmailSenderTests(PostgresFixture postgres)
{
    private sealed class Factory(DbContextOptions<ChefOsDbContext> options) : IDbContextFactory<ChefOsDbContext>
    {
        public ChefOsDbContext CreateDbContext() => new(options, new FixedTenantContext(null));
    }

    private static readonly DateTime Now = new(2026, 11, 12, 9, 30, 0, DateTimeKind.Utc);

    private async Task<(IdentityOutboxEmailSender Sender, DbContextOptions<ChefOsDbContext> Options)> CreateAsync()
    {
        var options = await postgres.CreateDatabaseAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        return (new IdentityOutboxEmailSender(new Factory(options), clock, new EmailDispatchSignal()), options);
    }

    private static async Task<OutgoingEmail> SingleAsync(DbContextOptions<ChefOsDbContext> options)
    {
        await using var db = new ChefOsDbContext(options, new FixedTenantContext(null));
        return await db.OutgoingEmails.AsNoTracking().SingleAsync();
    }

    [Fact]
    public async Task ConfirmationLink_IsQueuedInFrench_WithTheEncodedLink()
    {
        var (sender, options) = await CreateAsync();
        const string link = "https://back.test/Account/ConfirmEmail?userId=u1&code=a<b";

        await sender.SendConfirmationLinkAsync(new ApplicationUser(), "chef@resto.test", link);

        var email = await SingleAsync(options);
        Assert.Equal(EmailKind.AccountConfirmation, email.Kind);
        Assert.Null(email.RestaurantId);
        Assert.Equal("chef@resto.test", email.To);
        Assert.Equal("Confirmez votre adresse e-mail ChefOS", email.Subject);
        Assert.Equal(Now, email.CreatedAt);
        Assert.Contains(link, email.TextBody);
        Assert.NotNull(email.HtmlBody);
        Assert.Contains("userId=u1&amp;code=a&lt;b", email.HtmlBody);
        Assert.DoesNotContain("code=a<b", email.HtmlBody);
    }

    [Fact]
    public async Task PasswordResetLink_IsQueued()
    {
        var (sender, options) = await CreateAsync();
        const string link = "https://back.test/Account/ResetPassword?code=abc";

        await sender.SendPasswordResetLinkAsync(new ApplicationUser(), "chef@resto.test", link);

        var email = await SingleAsync(options);
        Assert.Equal(EmailKind.PasswordReset, email.Kind);
        Assert.Equal("Réinitialisez votre mot de passe ChefOS", email.Subject);
        Assert.Contains(link, email.TextBody);
        Assert.Null(email.RestaurantId);
    }

    [Fact]
    public async Task PasswordResetCode_IsQueued()
    {
        var (sender, options) = await CreateAsync();

        await sender.SendPasswordResetCodeAsync(new ApplicationUser(), "chef@resto.test", "CODE-123");

        var email = await SingleAsync(options);
        Assert.Equal(EmailKind.PasswordReset, email.Kind);
        Assert.Equal("Votre code de réinitialisation ChefOS", email.Subject);
        Assert.Contains("CODE-123", email.TextBody);
    }
}
