using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using ShopCoursework.Data;
using ShopCoursework.Models;
using ShopCoursework.Services.StockNotifications;

namespace ShopCoursework.Controllers;

[Route("Products")]
[AllowAnonymous]
public class ProductsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly StockNotificationService _stockNotificationService;

    public ProductsController(
        ApplicationDbContext db,
        StockNotificationService stockNotificationService)
    {
        _db = db;
        _stockNotificationService = stockNotificationService;
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

    [Authorize]
    [HttpPost("{productId:int}/StockNotifications")]
    public async Task<IActionResult> SubscribeToStock(
        int productId,
        string? size,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
            return Unauthorized();

        var normalizedSize = size?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedSize) || normalizedSize.Length > 32)
        {
            TempData["Error"] = "Выберите размер.";
            return RedirectToAction(nameof(Details), new { productId });
        }

        var productExists = await _db.Products
            .AsNoTracking()
            .AnyAsync(product => product.Id == productId, cancellationToken);
        if (!productExists)
            return NotFound();

        var hasSizeGuide = await _db.ProductSizeGuides
            .AnyAsync(guide =>
                guide.ProductId == productId &&
                guide.Size.ToUpper() == normalizedSize,
                cancellationToken);
        if (!hasSizeGuide)
        {
            TempData["Error"] = "Для выбранного размера нет таблицы мерок.";
            return RedirectToAction(nameof(Details), new { productId });
        }

        var availableStock = await _db.ProductVariants
            .Where(variant =>
                variant.ProductId == productId &&
                variant.Size.ToUpper() == normalizedSize &&
                variant.IsActive &&
                variant.Stock > 0)
            .SumAsync(variant => (int?)variant.Stock, cancellationToken) ?? 0;
        if (availableStock > 0)
        {
            TempData["Message"] = $"Размер {normalizedSize} уже есть в наличии.";
            return RedirectToAction(nameof(Details), new { productId });
        }

        var existing = await _db.StockNotifications
            .SingleOrDefaultAsync(notification =>
                notification.UserId == userId &&
                notification.ProductId == productId &&
                notification.Size == normalizedSize,
                cancellationToken);
        if (existing != null)
        {
            TempData["Message"] = $"Вы уже подписаны на размер {normalizedSize}.";
            return RedirectToAction(nameof(Details), new { productId });
        }

        _db.StockNotifications.Add(new StockNotification
        {
            UserId = userId,
            ProductId = productId,
            Size = normalizedSize
        });
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            await _stockNotificationService.NotifyAvailableSizeAsync(
                productId,
                normalizedSize,
                cancellationToken);
        }
        catch (DbUpdateException exception) when (
            exception.GetBaseException() is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            TempData["Message"] = $"Вы уже подписаны на размер {normalizedSize}.";
            return RedirectToAction(nameof(Details), new { productId });
        }

        TempData["Message"] = $"Сообщим в личном кабинете, когда размер {normalizedSize} появится.";
        return RedirectToAction(nameof(Details), new { productId });
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
