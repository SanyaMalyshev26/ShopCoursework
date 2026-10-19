using System.ComponentModel.DataAnnotations;

namespace ShopCoursework.Models;

public class Product
{
    public int Id { get; set; }
    [Required]
    [StringLength(120)]
    public string Name { get; set; } = "";
    [StringLength(2000)]
    public string? Description { get; set; }
    [Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal Price { get; set; }
    [Range(0, 5)]
    public double Rating { get; set; }
    [StringLength(2048)]
    public string ImageUrl { get; set; } = "https://placehold.co/300x250";
    public List<ProductVariant> Variants { get; set; } = new();
}