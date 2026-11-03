using ChefOS.Core.Receipts;

namespace ChefOS.Infrastructure.Receipts;

/// <summary>
/// Lecteur enregistré quand aucune clé API Anthropic n'est configurée : chaque lecture échoue avec un message qui dit quoi faire.
/// <see cref="Services.ReceiptService"/> met alors le ticket en <see cref="ReceiptStatus.Failed"/> avec ce message, et le chef peut saisir le ticket à la main ;
/// le back démarre quand même, sans clé.
/// </summary>
internal sealed class MissingKeyReceiptReader : IReceiptReader
{
    internal const string Message = "Clé API Anthropic non configurée : voir le README (dotnet user-secrets set Anthropic:ApiKey …)";

    public Task<ReceiptExtraction> ReadAsync(byte[] content, string contentType, CancellationToken ct = default) =>
        Task.FromException<ReceiptExtraction>(new InvalidOperationException(Message));
}
