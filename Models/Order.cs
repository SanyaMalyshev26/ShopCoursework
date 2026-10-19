namespace ShopCoursework.Models;

public class Order
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = OrderStatuses.New;
    public string PaymentMethod { get; set; } = PaymentMethods.CashOnDelivery;
    public string PaymentStatus { get; set; } = PaymentStatuses.Pending;
    public string? CheckoutRequestId { get; set; }
    public string? CheckoutRequestHash { get; set; }
    public decimal Total { get; set; }
    public string? ShippingAddressTitle { get; set; }
    public string? ShippingCity { get; set; }
    public string? ShippingStreet { get; set; }
    public List<OrderItem> Items { get; set; } = new();
}