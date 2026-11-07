using ChefOS.Core.Restaurants;

namespace ChefOS.Core.Vitrine;

public class VitrineImage : ITenantScoped
{
    public int Id { get; set; }
    public int RestaurantId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public int Position { get; set; }
    public bool IsCover { get; set; }
}
