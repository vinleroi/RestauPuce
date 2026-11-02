namespace ChefOS.Core.Restaurants;

public class Membership
{
    public string UserId { get; set; } = string.Empty;
    public int RestaurantId { get; set; }
    public Restaurant? Restaurant { get; set; }
    public MembershipRole Role { get; set; }
}
