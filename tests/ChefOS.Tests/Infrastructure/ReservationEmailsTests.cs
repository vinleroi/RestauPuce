using System.Net;
using ChefOS.Infrastructure.Notifications;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Gabarits des e-mails des réservations (texte et HTML), sans base de données.</summary>
public sealed class ReservationEmailsTests
{
    private static readonly ReservationEmailContext Base = new(
        RestaurantName: "Le Petit Bistro", AccentColor: "#2E7D32", PublicEmail: "contact@petitbistro.be", Phone: "02 123 45 67",
        Address: "Rue Haute 12, Bruxelles", Slug: "le-petit-bistro", BaseUrl: "https://resto.test",
        CustomerName: "Marie Dupont", CustomerEmail: "marie@example.com", Token: "abc",
        LocalDate: new DateOnly(2026, 11, 13), LocalTime: new TimeOnly(19, 30), ServiceName: "Soir", PartySize: 4,
        AmountPaid: 40m, RefundAmount: 0m, FreeCancellationUntilLocal: new DateTime(2026, 11, 12, 19, 30, 0), RestaurantId: 7);

    private static readonly Func<ReservationEmailContext, EmailMessage>[] All =
        [ReservationEmails.Confirmed, ReservationEmails.CancelledByCustomer, ReservationEmails.CancelledByRestaurant, ReservationEmails.LatePaymentRefunded];

    /// <summary>Le texte avec des espaces simples (les montants portent une espace insécable, comme partout ailleurs).</summary>
    private static string Plain(string value) => value.Replace(' ', ' ');

    /// <summary>Le HTML décodé et sans balises : ce que le client lit.</summary>
    private static string Visible(string html) =>
        Plain(WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")));

    [Fact]
    public void Confirmed_ContainsTheLocalDateTimeLinkAndDeadline()
    {
        var email = ReservationEmails.Confirmed(Base);

        Assert.Equal(EmailKind.ReservationConfirmed, email.Kind);
        Assert.Equal("marie@example.com", email.To);
        Assert.Equal(7, email.RestaurantId);
        Assert.Equal("Votre réservation chez Le Petit Bistro est confirmée", email.Subject);
        foreach (var body in new[] { Plain(email.TextBody), Visible(email.HtmlBody) })
        {
            Assert.Contains("13/11/2026", body);
            Assert.Contains("19:30", body);
            Assert.Contains("Soir", body);
            Assert.Contains("4 couverts", body);
            Assert.Contains("40,00 €", body);
            Assert.Contains("12/11/2026 à 19:30", body);
            Assert.Contains("Rue Haute 12, Bruxelles", body);
            Assert.Contains("02 123 45 67", body);
        }
        Assert.Contains("https://resto.test/le-petit-bistro/reservation/abc", email.TextBody);
        Assert.Contains("href=\"https://resto.test/le-petit-bistro/reservation/abc\"", email.HtmlBody);
    }

    [Fact]
    public void Confirmed_WithoutPayment_DoesNotMentionAnAmount()
    {
        var email = ReservationEmails.Confirmed(Base with { AmountPaid = 0m });

        Assert.DoesNotContain("€", email.TextBody);
        Assert.DoesNotContain("Montant", email.TextBody);
        Assert.Contains("12/11/2026 à 19:30", Plain(email.TextBody));
    }

    [Fact]
    public void ReplyTo_IsThePublicEmail_WhenPublished()
    {
        foreach (var template in All)
            Assert.Equal("contact@petitbistro.be", template(Base).ReplyTo);
    }

    [Fact]
    public void MissingContactDetails_AreOmitted()
    {
        var bare = Base with { PublicEmail = null, Phone = "  ", Address = null, AccentColor = null };

        foreach (var template in All)
        {
            var email = template(bare);
            Assert.Null(email.ReplyTo);
            Assert.DoesNotContain("null", email.TextBody);
            Assert.DoesNotContain("null", email.HtmlBody);
            Assert.DoesNotContain("Téléphone :", email.TextBody);
            Assert.DoesNotContain("Téléphone :", Visible(email.HtmlBody));
            Assert.DoesNotContain("Adresse :", email.TextBody);
            Assert.DoesNotContain("répondez", email.TextBody);
        }
    }

    [Fact]
    public void HostileInput_IsEscapedAndSubjectHasNoControlCharacters()
    {
        var hostile = Base with { CustomerName = "<script>alert(1)</script>", RestaurantName = "Chez\r\nBcc: x@y", Phone = "<b>02</b>", Address = "\"><img src=x>" };

        foreach (var template in All)
        {
            var email = template(hostile);
            Assert.DoesNotContain("<script>", email.HtmlBody);
            Assert.DoesNotContain("<b>02", email.HtmlBody);
            Assert.DoesNotContain("<img", email.HtmlBody);
            Assert.Contains("&lt;script&gt;", email.HtmlBody);
            Assert.DoesNotContain('\r', email.Subject);
            Assert.DoesNotContain('\n', email.Subject);
            Assert.All(email.Subject, c => Assert.False(char.IsControl(c)));
        }
    }

    [Fact]
    public void ReservationUrl_HandlesTrailingSlashAndEscapesTheSlug()
    {
        Assert.Equal("https://resto.test/caf%C3%A9/reservation/abc", ReservationEmails.ReservationUrl(Base with { BaseUrl = "https://resto.test/", Slug = "café" }));
        Assert.Equal("https://resto.test/le-petit-bistro/reservation/abc", ReservationEmails.ReservationUrl(Base));
    }

    [Fact]
    public void CancelledByCustomer_StatesTheRefund()
    {
        var refunded = ReservationEmails.CancelledByCustomer(Base with { RefundAmount = 40m });
        var kept = ReservationEmails.CancelledByCustomer(Base with { RefundAmount = 0m });

        Assert.Equal(EmailKind.ReservationCancelledByCustomer, refunded.Kind);
        Assert.Equal("Votre réservation chez Le Petit Bistro est annulée", refunded.Subject);
        Assert.Contains("40,00 €", Plain(refunded.TextBody));
        Assert.Contains("rembours", refunded.TextBody);
        Assert.DoesNotContain("sans remboursement", refunded.TextBody);
        Assert.Contains("sans remboursement", kept.TextBody);
        Assert.Contains("sans remboursement", Visible(kept.HtmlBody));
        Assert.Contains("13/11/2026", kept.TextBody);
    }

    [Fact]
    public void CancelledByRestaurant_AnnouncesTheRefundAndInvitesToContactTheRestaurant()
    {
        var email = ReservationEmails.CancelledByRestaurant(Base with { RefundAmount = 40m });
        var unpaid = ReservationEmails.CancelledByRestaurant(Base with { AmountPaid = 0m });

        Assert.Equal(EmailKind.ReservationCancelledByRestaurant, email.Kind);
        Assert.Equal("Le Petit Bistro a annulé votre réservation", email.Subject);
        Assert.Contains("40,00 €", Plain(email.TextBody));
        Assert.Contains("02 123 45 67", email.TextBody);
        Assert.Contains("contactez", email.TextBody);
        Assert.DoesNotContain("€", unpaid.TextBody);
    }

    [Fact]
    public void LatePaymentRefunded_SaysItCameTooLate_AndLinksToBookAgain()
    {
        var email = ReservationEmails.LatePaymentRefunded(Base with { RefundAmount = 40m });

        Assert.Equal(EmailKind.LatePaymentRefunded, email.Kind);
        Assert.Equal("Votre paiement à Le Petit Bistro a été remboursé", email.Subject);
        Assert.Contains("40,00 €", Plain(email.TextBody));
        Assert.Contains("trop tard", email.TextBody);
        Assert.Contains("https://resto.test/le-petit-bistro/reserver", email.TextBody);
        Assert.Contains("href=\"https://resto.test/le-petit-bistro/reserver\"", email.HtmlBody);
    }

    [Theory]
    [InlineData("#2E7D32", "#2E7D32")]
    [InlineData("#2e7d32", "#2E7D32")]
    [InlineData("red", "#8C3B2A")]
    [InlineData("#ff0000;background:url(x)", "#8C3B2A")]
    [InlineData(null, "#8C3B2A")]
    public void Accent_OnlyAHexColorIsUsed_OtherwiseTheVitrineDefault(string? accent, string expected)
    {
        var (_, html) = EmailLayout.Render("Titre", accent, ["Bonjour"], ("Voir", "https://resto.test/x"));

        Assert.Contains(expected, html);
        Assert.DoesNotContain("url(", html);
    }

    [Fact]
    public void CleanSubject_RemovesControlCharacters()
    {
        Assert.Equal("Chez Bcc: x@y", EmailLayout.CleanSubject("Chez\r\nBcc: x@y"));
        Assert.Equal("A B", EmailLayout.CleanSubject("A\tB\u0000"));
    }
}
