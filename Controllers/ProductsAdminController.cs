using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        var products = await _db.Products.OrderBy(p => p.Id).ToListAsync();
        return View(products);
    }

    // Форма добавления
    [HttpGet("Create")]
    public IActionResult Create()
    {
        return View("Form", new Product());
    }

    [HttpPost("Create")]
    public async Task<IActionResult> Create(Product product)
    {
        if (string.IsNullOrWhiteSpace(product.Name) || product.Price < 0)
        {
            ModelState.AddModelError("", "Укажите название и корректную цену");
            return View("Form", product);
        }

        product.Id = 0;
        if (string.IsNullOrWhiteSpace(product.ImageUrl))
            product.ImageUrl = "https://placehold.co/300x250";

        _db.Products.Add(product);
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
    public async Task<IActionResult> Edit(int id, Product input)
    {
        var product = await _db.Products.FindAsync(id);
        if (product == null) return NotFound();

        if (string.IsNullOrWhiteSpace(input.Name) || input.Price < 0)
        {
            ModelState.AddModelError("", "Укажите название и корректную цену");
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
}