namespace ShopCoursework.Models;

public sealed record AdminProductsViewModel(
    IReadOnlyList<Product> Products,
    int LowStockThreshold,
    int LowStockVariantCount,
    int OutOfStockVariantCount);
