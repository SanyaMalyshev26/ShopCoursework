using System.ComponentModel.DataAnnotations;

namespace ShopCoursework.Models;

public sealed class SizeRecommendationRequest : IValidatableObject
{
    [Range(typeof(decimal), "30", "200")]
    public decimal? Chest { get; init; }
    [Range(typeof(decimal), "30", "200")]
    public decimal? Waist { get; init; }
    [Range(typeof(decimal), "30", "200")]
    public decimal? Hips { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Chest == null && Waist == null && Hips == null)
            yield return new ValidationResult(
                "Укажите хотя бы одну мерку.",
                [nameof(Chest), nameof(Waist), nameof(Hips)]);
    }
}
