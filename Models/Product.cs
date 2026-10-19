namespace ShopCoursework.Models;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public double Rating { get; set; }
    public string ImageUrl { get; set; } = "https://placehold.co/300x250";
    public List<ProductVariant> Variants { get; set; } = new();
}