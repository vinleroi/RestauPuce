using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// Un serveur Mailpit local (conteneur) pour les tests du transport SMTP : SMTP sur 1025, API HTTP sur 8025, ports publiés au hasard.
/// Seuls les destinataires en <c>@client.test</c> sont acceptés : les autres sont refusés par une réponse 550.
/// Même image que le service <c>mailpit</c> de <c>docker-compose.yml</c>.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    public const string Image = "axllent/mailpit:v1.33.1";
    private const int Smtp = 1025;
    private const int Http = 8025;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(Smtp, true)
        .WithPortBinding(Http, true)
        .WithEnvironment("MP_SMTP_ALLOWED_RECIPIENTS", @"@client\.test$")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(Http).ForPath("/readyz")))
        .Build();

    public string Host => _container.Hostname;

    public int SmtpPort => _container.GetMappedPublicPort(Smtp);

    public Uri ApiBase => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(Http)}/");

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
