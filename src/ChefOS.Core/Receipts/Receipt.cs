using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Receipts;

public class Receipt : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int? SupplierId { get; set; }
    public string SupplierNameRaw { get; set; } = string.Empty;
    /// <summary>Date du ticket, en UTC.</summary>
    public DateTime? ReceiptDate { get; set; }
    public string? InvoiceNumber { get; set; }
    public bool AmountsIncludeVat { get; set; }
    public decimal? Total { get; set; }
    public ReceiptStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? AppliedAt { get; set; }
    public List<ReceiptLine> Lines { get; set; } = [];
}
