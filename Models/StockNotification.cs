using System.ComponentModel.DataAnnotations;

namespace ShopCoursework.Models;

public class StockNotification
{
    public int Id { get; set; }
    [Required]
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    [Required]
    [StringLength(32)]
    public string Size { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? NotifiedAt { get; set; }
}
