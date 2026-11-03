using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Receipts;

public class ReceiptFile : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public int ReceiptId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] Data { get; set; } = [];
}
