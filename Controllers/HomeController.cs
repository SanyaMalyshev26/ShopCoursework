using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ShopCoursework.Models;

namespace ShopCoursework.Controllers;

public class HomeController : Controller
{
    public IActionResult Index()
    {
        // Временные данные. Позже будут браться из базы через DbContext.
        var products = new List<Product>
        {
            new() { Id = 1, Name = "Джинсы",   Price = 3500,  Rating = 3.9 },
            new() { Id = 2, Name = "Куртка",   Price = 12900, Rating = 4.1 },
            new() { Id = 3, Name = "Футболка", Price = 1100,  Rating = 4.9 },
            new() { Id = 4, Name = "Свитер",   Price = 2990,  Rating = 4.75 },
        };
        return View(products);
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}