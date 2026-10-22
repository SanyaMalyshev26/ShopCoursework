using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;
using ShopCoursework.Models;
using ShopCoursework.Services.Csv;
using ShopCoursework.Services.Images;
using ShopCoursework.Services.StockNotifications;

namespace ShopCoursework.Controllers;

[Authorize(Roles = "Admin")]
[Route("Admin/Products")]
public class ProductsAdminController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly IImageProcessingClient _imageProcessingClient;
    private readonly StockNotificationService _stockNotificationService;
    private readonly ILogger<ProductsAdminController> _logger;
    private readonly int _lowStockThreshold;

    public ProductsAdminController(
        ApplicationDbContext db,
        IImageProcessingClient imageProcessingClient,
        StockNotificationService stockNotificationService,
        ILogger<ProductsAdminController> logger,
        IConfiguration configuration)
    {
        _db = db;
        _imageProcessingClient = imageProcessingClient;
        _stockNotificationService = stockNotificationService;
        _logger = logger;
        _lowStockThreshold = configuration.GetValue<int?>("Inventory:LowStockThreshold") ?? 5;
        if (_lowStockThreshold < 1)
            throw new InvalidOperationException("Inventory:LowStockThreshold must be greater than zero.");
    }

    // Список товаров
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var products = await _db.Products
            .Include(product => product.Variants)
            .OrderBy(product => product.Id)
            .ToListAsync();
        var activeVariants = products.SelectMany(product => product.Variants)
            .Where(variant => variant.IsActive)
            .ToList();
        return View(new AdminProductsViewModel(
            products,
            _lowStockThreshold,
            activeVariants.Count(variant => variant.Stock > 0 && variant.Stock <= _lowStockThreshold),
            activeVariants.Count(variant => variant.Stock == 0)));
    }

    [HttpGet("Inventory/Csv")]
    public async Task<IActionResult> InventoryCsv(CancellationToken cancellationToken)
    {
        var rows = await _db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive && variant.Stock <= _lowStockThreshold)
            .OrderBy(variant => variant.Stock)
            .ThenBy(variant => variant.Product!.Name)
            .ThenBy(variant => variant.Sku)
            .Select(variant => new
            {
                variant.ProductId,
                ProductName = variant.Product!.Name,
                variant.Sku,
                variant.Size,
                variant.Color,
                variant.Stock
            })
            .ToListAsync(cancellationToken);

        var csvRows = new List<string>
        {
            CsvExport.Row("ID товара", "Товар", "SKU", "Размер", "Цвет", "Остаток", "Порог")
        };
        csvRows.AddRange(rows.Select(variant => CsvExport.Row(
            variant.ProductId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            variant.ProductName,
            variant.Sku,
            variant.Size,
            variant.Color,
            variant.Stock.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _lowStockThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        var csv = string.Join("\r\n", csvRows) + "\r\n";
        return File(
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv),
            "text/csv; charset=utf-8",
            $"inventory-restock-{DateTime.UtcNow:yyyy-MM-dd}.csv");
    }

    [HttpPost("Images")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10 * 1024 * 1024 + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024 + 64 * 1024)]
    public async Task<IActionResult> UploadImage(
        [FromForm(Name = "image")] IFormFile? image,
        CancellationToken cancellationToken)
    {
        const long maxImageBytes = 10 * 1024 * 1024;
        if (image == null || image.Length == 0 || image.Length > maxImageBytes)
            return BadRequest(new { message = "Выберите изображение размером не более 10 МБ." });

        if (image.ContentType is not ("image/jpeg" or "image/png" or "image/webp"))
            return BadRequest(new { message = "Поддерживаются только JPEG, PNG и WebP." });

        try
        {
            var processed = await _imageProcessingClient.ProcessAsync(image, cancellationToken);
            return Ok(processed);
        }
        catch (ImageProcessingException exception) when (
            exception.StatusCode is HttpStatusCode.BadRequest or
                HttpStatusCode.RequestEntityTooLarge or
                HttpStatusCode.UnsupportedMediaType or
                HttpStatusCode.UnprocessableEntity)
        {
            return BadRequest(new { message = "Файл не является корректным поддерживаемым изображением." });
        }
        catch (ImageProcessingException exception)
        {
            _logger.LogError(exception, "Image processing service returned an invalid response.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Сервис обработки изображений временно недоступен."
            });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "Image processing service could not be reached.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Сервис обработки изображений временно недоступен."
            });
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Image processing service request timed out.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Сервис обработки изображений не ответил вовремя."
            });
        }
    }

    // Форма добавления
    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View("Form", new Product());
    }

    [HttpPost("Create")]
    public async Task<IActionResult> Create(
        [Bind(nameof(Product.Name), nameof(Product.Description), nameof(Product.Price), nameof(Product.Rating), nameof(Product.ImageUrl))]
        Product product)
    {
        var input = new Product
        {
            Name = product.Name?.Trim() ?? "",
            Description = product.Description?.Trim(),
            Price = product.Price,
            Rating = product.Rating,
            ImageUrl = product.ImageUrl?.Trim() ?? ""
        };
        if (!ValidateProduct(input))
        {
            input.Id = 0;
            return View("Form", input);
        }

        if (string.IsNullOrWhiteSpace(input.ImageUrl))
            input.ImageUrl = "https://placehold.co/300x250";

        _db.Products.Add(input);
        await _db.SaveChangesAsync();
        TempData["Message"] = "Товар добавлен";
        return RedirectToAction("Index");
    }

    // Форма редактирования
    [HttpGet("Edit/{id:int}")]
    public async Task<IActionResult> Edit(int id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();
        return View("Form", product);
    }

    [HttpPost("Edit/{id:int}")]
    public async Task<IActionResult> Edit(
        int id,
        [Bind(nameof(Product.Name), nameof(Product.Description), nameof(Product.Price), nameof(Product.Rating), nameof(Product.ImageUrl))]
        Product input)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();

        input.Name = input.Name?.Trim() ?? "";
        input.Description = input.Description?.Trim();
        input.ImageUrl = input.ImageUrl?.Trim() ?? "";
        if (!ValidateProduct(input))
        {
            input.Id = id;
            return View("Form", input);
        }

        product.Name = input.Name.Trim();
        product.Description = input.Description;
        product.Price = input.Price;
        product.Rating = input.Rating;
        product.ImageUrl = string.IsNullOrWhiteSpace(input.ImageUrl)
            ? "https://placehold.co/300x250"
            : input.ImageUrl.Trim();

        await _db.SaveChangesAsync();
        TempData["Message"] = "Товар сохранён";
        return RedirectToAction("Index");
    }

    [HttpGet("{id:int}/Variants")]
    public async Task<IActionResult> Variants(int id)
    {
        var product = await _db.Products
            .Include(item => item.Variants.OrderBy(variant => variant.Size).ThenBy(variant => variant.Color))
            .SingleOrDefaultAsync(item => item.Id == id);
        if (product == null) return NotFound();

        return View("Variants", product);
    }

    [HttpGet("{id:int}/SizeGuides")]
    public async Task<IActionResult> SizeGuides(int id)
    {
        var product = await _db.Products
            .Include(item => item.SizeGuides.OrderBy(guide => guide.Size))
            .SingleOrDefaultAsync(item => item.Id == id);
        if (product == null) return NotFound();

        return View(product);
    }

    [HttpPost("{id:int}/SizeGuides")]
    public async Task<IActionResult> AddSizeGuide(int id, ProductSizeGuideInput input)
    {
        if (!await _db.Products.AnyAsync(product => product.Id == id))
            return NotFound();

        input.Size = input.Size?.Trim().ToUpperInvariant() ?? "";
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(input.Size))
        {
            TempData["Error"] = "Проверьте размер и диапазоны мерок (30–200 см; минимум не больше максимума).";
            return RedirectToAction(nameof(SizeGuides), new { id });
        }

        if (await _db.ProductSizeGuides.AnyAsync(guide =>
                guide.ProductId == id && guide.Size == input.Size))
        {
            TempData["Error"] = $"Таблица для размера {input.Size} уже существует.";
            return RedirectToAction(nameof(SizeGuides), new { id });
        }

        _db.ProductSizeGuides.Add(CreateSizeGuide(id, input));
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException exception) when (
            exception.GetBaseException() is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            TempData["Error"] = $"Таблица для размера {input.Size} уже существует.";
            return RedirectToAction(nameof(SizeGuides), new { id });
        }

        TempData["Message"] = $"Таблица размера {input.Size} добавлена.";
        return RedirectToAction(nameof(SizeGuides), new { id });
    }

    [HttpPost("{id:int}/SizeGuides/{guideId:int}/Update")]
    public async Task<IActionResult> UpdateSizeGuide(int id, int guideId, ProductSizeGuideInput input)
    {
        var guide = await _db.ProductSizeGuides
            .SingleOrDefaultAsync(item => item.Id == guideId && item.ProductId == id);
        if (guide == null) return NotFound();

        input.Size = input.Size?.Trim().ToUpperInvariant() ?? "";
        if (!ModelState.IsValid || string.IsNullOrWhiteSpace(input.Size))
        {
            TempData["Error"] = "Проверьте размер и диапазоны мерок (30–200 см; минимум не больше максимума).";
            return RedirectToAction(nameof(SizeGuides), new { id });
        }

        if (await _db.ProductSizeGuides.AnyAsync(item =>
                item.ProductId == id && item.Id != guideId && item.Size == input.Size))
        {
            TempData["Error"] = $"Таблица для размера {input.Size} уже существует.";
            return RedirectToAction(nameof(SizeGuides), new { id });
        }

        guide.Size = input.Size;
        CopyMeasurements(guide, input);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException exception) when (
            exception.GetBaseException() is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            TempData["Error"] = $"Таблица для размера {input.Size} уже существует.";
            return RedirectToAction(nameof(SizeGuides), new { id });
        }

        TempData["Message"] = $"Таблица размера {input.Size} сохранена.";
        return RedirectToAction(nameof(SizeGuides), new { id });
    }

    [HttpPost("{id:int}/SizeGuides/{guideId:int}/Delete")]
    public async Task<IActionResult> DeleteSizeGuide(int id, int guideId)
    {
        var guide = await _db.ProductSizeGuides
            .SingleOrDefaultAsync(item => item.Id == guideId && item.ProductId == id);
        if (guide == null) return NotFound();

        _db.ProductSizeGuides.Remove(guide);
        await _db.SaveChangesAsync();
        TempData["Message"] = $"Таблица размера {guide.Size} удалена.";
        return RedirectToAction(nameof(SizeGuides), new { id });
    }

    [HttpPost("{id:int}/Variants")]
    public async Task<IActionResult> AddVariant(int id, ProductVariant input)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();

        input.Sku = input.Sku?.Trim().ToUpperInvariant() ?? "";
        input.Size = input.Size?.Trim() ?? "";
        input.Color = input.Color?.Trim() ?? "";

        input.Id = 0;
        input.Product = null;
        if (!ModelState.IsValid ||
            string.IsNullOrWhiteSpace(input.Sku) || input.Sku.Length > 64 ||
            string.IsNullOrWhiteSpace(input.Size) || input.Size.Length > 32 ||
            string.IsNullOrWhiteSpace(input.Color) || input.Color.Length > 64 ||
            input.Stock < 0)
        {
            TempData["Error"] = "Проверьте SKU, размер, цвет и неотрицательный остаток.";
            return RedirectToAction(nameof(Variants), new { id });
        }

        if (await _db.ProductVariants.AnyAsync(variant => variant.Sku == input.Sku))
        {
            TempData["Error"] = $"SKU {input.Sku} уже используется.";
            return RedirectToAction(nameof(Variants), new { id });
        }

        var variant = new ProductVariant
        {
            ProductId = id,
            Sku = input.Sku,
            Size = input.Size,
            Color = input.Color,
            Stock = input.Stock,
            IsActive = true
        };

        await using var transaction = await _db.Database.BeginTransactionAsync();
        _db.ProductVariants.Add(variant);
        try
        {
            await _db.SaveChangesAsync();
            await _stockNotificationService.NotifyAvailableSizeAsync(id, variant.Size);
            await transaction.CommitAsync();
        }
        catch (DbUpdateException exception) when (
            exception.GetBaseException() is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            TempData["Error"] = $"SKU {input.Sku} уже используется.";
            return RedirectToAction(nameof(Variants), new { id });
        }

        TempData["Message"] = "Вариант добавлен";
        return RedirectToAction(nameof(Variants), new { id });
    }

    [HttpPost("{id:int}/Variants/{variantId:int}/Update")]
    public async Task<IActionResult> UpdateVariant(
        int id,
        int variantId,
        int stock,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var variant = await _db.ProductVariants
            .SingleOrDefaultAsync(item => item.Id == variantId && item.ProductId == id);
        if (variant == null) return NotFound();
        if (!ModelState.IsValid || stock < 0)
        {
            TempData["Error"] = "Остаток не может быть отрицательным.";
            return RedirectToAction(nameof(Variants), new { id });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        variant.Stock = stock;
        variant.IsActive = isActive;
        await _db.SaveChangesAsync(cancellationToken);
        await _stockNotificationService.NotifyAvailableSizeAsync(id, variant.Size, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        TempData["Message"] = "Остаток обновлён";
        return RedirectToAction(nameof(Variants), new { id });
    }

    [HttpPost("{id:int}/Variants/{variantId:int}/Delete")]
    public async Task<IActionResult> DeleteVariant(int id, int variantId)
    {
        var variant = await _db.ProductVariants
            .SingleOrDefaultAsync(item => item.Id == variantId && item.ProductId == id);
        if (variant == null) return NotFound();

        if (await _db.OrderItems.AnyAsync(item => item.ProductVariantId == variantId))
        {
            TempData["Error"] = "Вариант уже есть в заказах. Отключите его вместо удаления.";
            return RedirectToAction(nameof(Variants), new { id });
        }

        _db.ProductVariants.Remove(variant);
        await _db.SaveChangesAsync();
        TempData["Message"] = "Вариант удалён";
        return RedirectToAction(nameof(Variants), new { id });
    }

    // Удаление
    [HttpPost("Delete/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();

        // Товар из существующих заказов удалять нельзя, иначе испортим историю заказов
        var usedInOrders = await _db.OrderItems.AnyAsync(i => i.ProductId == id);
        if (usedInOrders)
        {
            TempData["Error"] = "Этот товар есть в заказах, удалить его нельзя";
            return RedirectToAction("Index");
        }

        _db.Products.Remove(product);
        await _db.SaveChangesAsync();
        TempData["Message"] = "Товар удалён";
        return RedirectToAction("Index");
    }

    private bool ValidateProduct(Product product)
    {
        if (string.IsNullOrWhiteSpace(product.Name))
            ModelState.AddModelError(nameof(product.Name), "Укажите название товара.");
        if (!ModelState.IsValid)
            return false;
        if (!double.IsFinite(product.Rating))
        {
            ModelState.AddModelError(nameof(product.Rating), "Рейтинг должен быть числом от 0 до 5.");
            return false;
        }
        if (!IsAllowedImageUrl(product.ImageUrl))
        {
            ModelState.AddModelError(nameof(product.ImageUrl), "Для изображения укажите HTTP(S)-ссылку или локальный путь.");
            return false;
        }

        return true;
    }

    private static ProductSizeGuide CreateSizeGuide(int productId, ProductSizeGuideInput input)
    {
        var guide = new ProductSizeGuide
        {
            ProductId = productId,
            Size = input.Size
        };
        CopyMeasurements(guide, input);
        return guide;
    }

    private static void CopyMeasurements(ProductSizeGuide guide, ProductSizeGuideInput input)
    {
        guide.ChestMin = input.ChestMin;
        guide.ChestMax = input.ChestMax;
        guide.WaistMin = input.WaistMin;
        guide.WaistMax = input.WaistMax;
        guide.HipsMin = input.HipsMin;
        guide.HipsMax = input.HipsMax;
    }

    private static bool IsAllowedImageUrl(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return true;

        if (imageUrl.StartsWith('/') &&
            !imageUrl.StartsWith("//", StringComparison.Ordinal) &&
            !imageUrl.Contains('\\'))
            return true;

        return Uri.TryCreate(imageUrl, UriKind.Absolute, out var absoluteUri) &&
               (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps);
    }
}