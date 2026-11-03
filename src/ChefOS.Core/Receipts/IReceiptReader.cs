namespace ChefOS.Core.Receipts;

public interface IReceiptReader
{
    Task<ReceiptExtraction> ReadAsync(byte[] content, string contentType, CancellationToken ct = default);
}
