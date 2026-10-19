namespace ShopCoursework.Models;

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public string? ProductName { get; set; }
    public int? ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }
    public string? VariantSku { get; set; }
    public string? VariantSize { get; set; }
    public string? VariantColor { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
}