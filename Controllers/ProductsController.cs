using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;
using ShopCoursework.Models;

namespace ShopCoursework.Controllers;

[Route("Products")]
[AllowAnonymous]
public class ProductsController : Controller
{
    private readonly ApplicationDbContext _db;

    public ProductsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet("{productId:int}")]
    public async Task<IActionResult> Details(int productId, CancellationToken cancellationToken)
    {
        var product = await _db.Products
            .AsNoTracking()
            .Include(item => item.Variants.Where(variant => variant.IsActive))
            .Include(item => item.SizeGuides)
            .SingleOrDefaultAsync(item => item.Id == productId, cancellationToken);
        if (product == null)
            return NotFound();

        return View(product);
    }

    [HttpPost("{productId:int}/SizeRecommendation")]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> RecommendSize(
        int productId,
        [FromBody] SizeRecommendationRequest? request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (request == null || !TryValidateModel(request))
            return BadRequest(new { message = "Укажите хотя бы одну мерку от 30 до 200 см." });

        var productExists = await _db.Products
            .AsNoTracking()
            .AnyAsync(product => product.Id == productId, cancellationToken);
        if (!productExists)
            return NotFound(new { message = "Товар не найден." });

        var sizeGuides = await _db.ProductSizeGuides
            .AsNoTracking()
            .Where(guide => guide.ProductId == productId)
            .ToListAsync(cancellationToken);
        if (sizeGuides.Count == 0)
            return Ok(new
            {
                recommendedSize = (string?)null,
                availableStock = 0,
                matchingSizes = Array.Empty<object>(),
                message = "Таблица размеров для этой модели ещё не заполнена."
            });

        var availableSizes = await _db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.ProductId == productId && variant.IsActive && variant.Stock > 0)
            .GroupBy(variant => variant.Size.ToUpper())
            .Select(group => new { Size = group.Key, Stock = group.Sum(variant => variant.Stock) })
            .ToDictionaryAsync(item => item.Size, item => item.Stock, cancellationToken);

        var matchingSizes = sizeGuides
            .Where(guide =>
                (!request.Chest.HasValue || request.Chest.Value >= guide.ChestMin && request.Chest.Value <= guide.ChestMax) &&
                (!request.Waist.HasValue || request.Waist.Value >= guide.WaistMin && request.Waist.Value <= guide.WaistMax) &&
                (!request.Hips.HasValue || request.Hips.Value >= guide.HipsMin && request.Hips.Value <= guide.HipsMax))
            .Where(guide => availableSizes.ContainsKey(guide.Size))
            .Select(guide => new
            {
                guide.Size,
                AvailableStock = availableSizes[guide.Size]
            })
            .OrderByDescending(candidate => candidate.AvailableStock)
            .ThenBy(candidate => GetStandardSizeRank(candidate.Size))
            .ThenBy(candidate => candidate.Size, StringComparer.Ordinal)
            .ToList();

        if (matchingSizes.Count == 0)
        {
            return Ok(new
            {
                recommendedSize = (string?)null,
                availableStock = 0,
                matchingSizes,
                message = "Подходящего размера в наличии нет."
            });
        }

        var recommendation = matchingSizes[0];
        return Ok(new
        {
            recommendedSize = recommendation.Size,
            availableStock = recommendation.AvailableStock,
            matchingSizes,
            message = "Рекомендация ориентировочная. Проверьте таблицу размеров перед заказом."
        });
    }

    private static int GetStandardSizeRank(string size)
    {
        var standardSizes = new[]
        {
            "XXXS", "XXS", "XS", "S", "M", "L", "XL", "XXL", "XXXL", "4XL", "5XL"
        };
        var index = Array.IndexOf(standardSizes, size);
        if (index >= 0)
            return index;

        return int.TryParse(size, out var numericSize)
            ? 100 + numericSize
            : 10_000;
    }
}
