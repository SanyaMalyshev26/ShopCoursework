using System.ComponentModel.DataAnnotations;

namespace ShopCoursework.Models;

public class ProductVariant
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    [Required]
    [StringLength(64)]
    public string Sku { get; set; } = "";
    [Required]
    [StringLength(32)]
    public string Size { get; set; } = "";
    [Required]
    [StringLength(64)]
    public string Color { get; set; } = "";
    [Range(0, int.MaxValue)]
    public int Stock { get; set; }
    public bool IsActive { get; set; } = true;
}
