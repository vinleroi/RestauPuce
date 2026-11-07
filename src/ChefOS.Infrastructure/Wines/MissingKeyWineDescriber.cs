namespace ChefOS.Infrastructure.Wines;

/// <summary>Describer enregistré quand aucune clé API Anthropic n'est configurée : la rédaction échoue avec un message qui dit quoi faire (le chef saisit la fiche à la main).</summary>
internal sealed class MissingKeyWineDescriber : IWineDescriber
{
    internal const string Message = "Clé API Anthropic non configurée : voir le README (dotnet user-secrets set Anthropic:ApiKey …)";

    public Task<WineDraft> DescribeAsync(WineDescribeRequest request, CancellationToken ct = default) =>
        Task.FromException<WineDraft>(new InvalidOperationException(Message));
}
