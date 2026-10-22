using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;
using ShopCoursework.Models;

namespace ShopCoursework.Controllers;

public class HomeController : Controller
{
    private readonly ApplicationDbContext _db;

    public HomeController(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index(
        string? search,
        string? size,
        bool inStockOnly = false,
        string sort = "featured")
    {
        search = search?.Trim();
        size = size?.Trim();
        if (search?.Length > 120 || size?.Length > 32 ||
            sort is not ("featured" or "price-asc" or "price-desc" or "rating"))
        {
            return BadRequest();
        }

        var query = _db.Products
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrEmpty(size))
        {
            var selectedSize = size.ToUpperInvariant();
            query = query.Where(product => product.Variants.Any(variant =>
                variant.IsActive && variant.Size.ToUpper() == selectedSize &&
                (!inStockOnly || variant.Stock > 0)));
        }
        else if (inStockOnly)
        {
            query = query.Where(product =>
                product.Variants.Any(variant => variant.IsActive && variant.Stock > 0));
        }

        var products = await query
            .Include(product => product.Variants.Where(variant => variant.IsActive))
            .ToListAsync();
        if (!string.IsNullOrEmpty(search))
        {
            products = products
                .Where(product =>
                    product.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    product.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
                .ToList();
        }

        products = sort switch
        {
            "price-asc" => products.OrderBy(product => product.Price).ThenBy(product => product.Id).ToList(),
            "price-desc" => products.OrderByDescending(product => product.Price).ThenBy(product => product.Id).ToList(),
            "rating" => products.OrderByDescending(product => product.Rating).ThenBy(product => product.Id).ToList(),
            _ => products.OrderBy(product => product.Id).ToList()
        };
        var sizes = await _db.ProductVariants
            .AsNoTracking()
            .Where(variant => variant.IsActive)
            .Select(variant => variant.Size)
            .Distinct()
            .OrderBy(value => value)
            .ToListAsync();

        return View(new CatalogViewModel
        {
            Products = products,
            Sizes = sizes,
            Search = search ?? "",
            Size = size ?? "",
            Sort = sort,
            InStockOnly = inStockOnly
        });
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}