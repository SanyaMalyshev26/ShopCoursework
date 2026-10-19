using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;

namespace ShopCoursework.Controllers;

public class CartController : Controller
{
    private readonly ApplicationDbContext _db;

    public CartController(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IActionResult> Index()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            ViewBag.Addresses = await _db.Addresses
                .Where(address => address.UserId == userId)
                .OrderByDescending(address => address.IsDefault)
                .ThenBy(address => address.Id)
                .ToListAsync();
        }

        return View();
    }
}