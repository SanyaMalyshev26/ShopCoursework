namespace ShopCoursework.Models;

public class ProductVariant
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public string Sku { get; set; } = "";
    public string Size { get; set; } = "";
    public string Color { get; set; } = "";
    public int Stock { get; set; }
    public bool IsActive { get; set; } = true;
}
