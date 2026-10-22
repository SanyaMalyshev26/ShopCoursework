using System.ComponentModel.DataAnnotations;

namespace ShopCoursework.Models;

public class ProductSizeGuide
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    [Required]
    [StringLength(32)]
    public string Size { get; set; } = "";
    [Range(typeof(decimal), "30", "200")]
    public decimal ChestMin { get; set; }
    [Range(typeof(decimal), "30", "200")]
    public decimal ChestMax { get; set; }
    [Range(typeof(decimal), "30", "200")]
    public decimal WaistMin { get; set; }
    [Range(typeof(decimal), "30", "200")]
    public decimal WaistMax { get; set; }
    [Range(typeof(decimal), "30", "200")]
    public decimal HipsMin { get; set; }
    [Range(typeof(decimal), "30", "200")]
    public decimal HipsMax { get; set; }
}
