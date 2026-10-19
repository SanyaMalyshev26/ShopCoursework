namespace ShopCoursework.Models;

public class Order
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = OrderStatuses.New;
    public decimal Total { get; set; }
    public List<OrderItem> Items { get; set; } = new();
}