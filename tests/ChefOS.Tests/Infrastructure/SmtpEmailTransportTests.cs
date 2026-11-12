using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ChefOS.Infrastructure.Notifications;
using Microsoft.Extensions.Options;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Transport MailKit contre un Mailpit local : livraison (texte, HTML, Reply-To) et classement des erreurs. Aucun serveur externe.</summary>
public class SmtpEmailTransportTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    private SmtpEmailTransport Transport(string? host = null, int? port = null) => new(Options.Create(new EmailOptions
    {
        Sender = EmailOptions.SmtpSender,
        FromAddress = "reservations@chefos.test",
        FromName = "ChefOS",
        Smtp = new SmtpOptions { Host = host ?? mailpit.Host, Port = port ?? mailpit.SmtpPort, Security = SmtpSecurity.None }
    }));

    private static EmailMessage Message(string to, string subject) => new(
        EmailKind.ReservationConfirmed, to, subject, "Bonjour Jean, votre table est confirmée.",
        "<p>Bonjour Jean, votre table est <strong>confirmée</strong>.</p>", "bistro@resto.test", 7);

    [Fact]
    public async Task Send_DeliversTextHtmlAndReplyTo()
    {
        var subject = $"Votre réservation chez Bistro est confirmée {Guid.NewGuid():N}";

        await Transport().SendAsync(Message("jean@client.test", subject), default);

        using var http = new HttpClient { BaseAddress = mailpit.ApiBase };
        var list = await http.GetFromJsonAsync<JsonElement>("api/v1/messages");
        var id = list.GetProperty("messages").EnumerateArray()
            .Single(m => m.GetProperty("Subject").GetString() == subject).GetProperty("ID").GetString();
        var message = await http.GetFromJsonAsync<JsonElement>($"api/v1/message/{id}");

        Assert.Equal(subject, message.GetProperty("Subject").GetString());
        Assert.Equal("reservations@chefos.test", message.GetProperty("From").GetProperty("Address").GetString());
        Assert.Equal("ChefOS", message.GetProperty("From").GetProperty("Name").GetString());
        Assert.Equal("jean@client.test", Assert.Single(message.GetProperty("To").EnumerateArray()).GetProperty("Address").GetString());
        Assert.Equal("bistro@resto.test", Assert.Single(message.GetProperty("ReplyTo").EnumerateArray()).GetProperty("Address").GetString());
        Assert.Contains("Bonjour Jean, votre table est confirmée.", message.GetProperty("Text").GetString());
        Assert.Contains("<strong>confirmée</strong>", message.GetProperty("HTML").GetString());
    }

    [Fact]
    public async Task RejectedRecipient_IsPermanent()
    {
        var ex = await Assert.ThrowsAsync<EmailTransportException>(() =>
            Transport().SendAsync(Message("x@ailleurs.test", "Refusé"), default));

        Assert.Equal(EmailFailure.Permanent, ex.Failure);
        Assert.Contains("550", ex.Message);
        Assert.DoesNotContain("@", ex.Message); // la raison finit dans LastError : jamais d'adresse
    }

    [Fact]
    public async Task UnreachableServer_IsServerUnavailable()
    {
        var ex = await Assert.ThrowsAsync<EmailTransportException>(() =>
            Transport("127.0.0.1", ClosedPort()).SendAsync(Message("jean@client.test", "Injoignable"), default));

        Assert.Equal(EmailFailure.ServerUnavailable, ex.Failure);
    }

    [Fact]
    public async Task SilentServer_CancelledByTheCaller_ThrowsOperationCanceled()
    {
        // Un serveur qui accepte la connexion sans jamais saluer : l'appelant (le dispatcher et son délai de 30 s) annule.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Transport("127.0.0.1", port).SendAsync(Message("jean@client.test", "Silence"), cts.Token));
    }

    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
