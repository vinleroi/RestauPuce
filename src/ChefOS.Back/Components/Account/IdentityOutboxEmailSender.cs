using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using ChefOS.Infrastructure.Notifications;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Back.Components.Account;

/// <summary>
/// E-mails d'Identity (confirmation d'adresse, mot de passe oublié) : ils entrent dans la file globale, comme ceux des réservations,
/// et partent par le même dispatcher. Aucun restaurant n'est attaché (à l'inscription, il n'en existe pas encore).
/// Le lien reçu est le lien brut : le gabarit l'échappe lui-même dans le HTML (les pages ne l'encodent plus).
/// </summary>
internal sealed class IdentityOutboxEmailSender(IDbContextFactory<ChefOsDbContext> factory, TimeProvider clock, EmailDispatchSignal signal)
    : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        QueueAsync(EmailKind.AccountConfirmation, email,
            "Confirmez votre adresse e-mail ChefOS",
            ["Bienvenue sur ChefOS. Confirmez votre adresse e-mail pour activer votre compte.",
             "Si vous n'êtes pas à l'origine de cette demande, ignorez simplement ce message."],
            ("Confirmer mon adresse", confirmationLink));

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        QueueAsync(EmailKind.PasswordReset, email,
            "Réinitialisez votre mot de passe ChefOS",
            ["Vous avez demandé à réinitialiser votre mot de passe ChefOS.",
             "Si vous n'êtes pas à l'origine de cette demande, ignorez ce message : votre mot de passe reste inchangé."],
            ("Choisir un nouveau mot de passe", resetLink));

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        QueueAsync(EmailKind.PasswordReset, email,
            "Votre code de réinitialisation ChefOS",
            ["Voici votre code pour réinitialiser votre mot de passe ChefOS :", resetCode,
             "Si vous n'êtes pas à l'origine de cette demande, ignorez ce message."],
            null);

    private async Task QueueAsync(EmailKind kind, string to, string subject, IReadOnlyList<string> paragraphs, (string Label, string Url)? button)
    {
        var (text, html) = EmailLayout.Render(subject, null, paragraphs, button);
        await using var db = await factory.CreateDbContextAsync();
        // Pas de transaction d'événement ici : on enregistre tout de suite, puis on réveille le dispatcher.
        if (EmailOutbox.Enqueue(db, new EmailMessage(kind, to, subject, text, html), clock.GetUtcNow().UtcDateTime))
        {
            await db.SaveChangesAsync();
            signal.Notify();
        }
    }
}
