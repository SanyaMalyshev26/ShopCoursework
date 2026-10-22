using System.ComponentModel.DataAnnotations;

namespace ShopCoursework.Models;

public sealed class ProductSizeGuideInput : IValidatableObject
{
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ChestMin > ChestMax)
            yield return new ValidationResult("Минимальная мерка груди не может быть больше максимальной.");
        if (WaistMin > WaistMax)
            yield return new ValidationResult("Минимальная мерка талии не может быть больше максимальной.");
        if (HipsMin > HipsMax)
            yield return new ValidationResult("Минимальная мерка бёдер не может быть больше максимальной.");
    }
}
