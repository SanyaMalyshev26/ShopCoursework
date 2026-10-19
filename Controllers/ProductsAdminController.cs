using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;
using ShopCoursework.Models;

namespace ShopCoursework.Controllers;

[Authorize(Roles = "Admin")]
[Route("Admin/Products")]
public class ProductsAdminController : Controller
{
    private readonly ApplicationDbContext _db;

    public ProductsAdminController(ApplicationDbContext db)
    {
        _db = db;
    }

    // Список товаров
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var products = await _db.Products
            .Include(product => product.Variants)
            .OrderBy(product => product.Id)
            .ToListAsync();
        return View(products);
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

        _db.ProductVariants.Add(new ProductVariant
        {
            ProductId = id,
            Sku = input.Sku,
            Size = input.Size,
            Color = input.Color,
            Stock = input.Stock,
            IsActive = true
        });

        try
        {
            await _db.SaveChangesAsync();
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
    public async Task<IActionResult> UpdateVariant(int id, int variantId, int stock, bool isActive)
    {
        var variant = await _db.ProductVariants
            .SingleOrDefaultAsync(item => item.Id == variantId && item.ProductId == id);
        if (variant == null) return NotFound();
        if (!ModelState.IsValid || stock < 0)
        {
            TempData["Error"] = "Остаток не может быть отрицательным.";
            return RedirectToAction(nameof(Variants), new { id });
        }

        variant.Stock = stock;
        variant.IsActive = isActive;
        await _db.SaveChangesAsync();

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