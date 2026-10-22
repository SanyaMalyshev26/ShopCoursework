namespace ShopCoursework.Models;

public sealed class CatalogViewModel
{
    public IReadOnlyList<Product> Products { get; init; } = Array.Empty<Product>();
    public IReadOnlyList<string> Sizes { get; init; } = Array.Empty<string>();
    public string Search { get; init; } = "";
    public string Size { get; init; } = "";
    public string Sort { get; init; } = "featured";
    public bool InStockOnly { get; init; }
}
