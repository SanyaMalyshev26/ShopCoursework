using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;
using ShopCoursework.Models;

namespace ShopCoursework.Controllers;

public class CartLine
{
    public int Id { get; set; }
    public int Qty { get; set; }
}

public class OrdersController : Controller
{
    private readonly ApplicationDbContext _db;

    public OrdersController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] List<CartLine> lines)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized();

        if (lines == null || lines.Count == 0 || lines.Any(l => l.Qty < 1))
            return BadRequest();

        var ids = lines.Select(l => l.Id).Distinct().ToList();
        var products = await _db.Products
            .Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        if (ids.Any(id => !products.ContainsKey(id)))
            return BadRequest();

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value;

        var order = new Order { UserId = userId };
        foreach (var line in lines)
        {
            var product = products[line.Id];
            order.Items.Add(new OrderItem
            {
                ProductId = product.Id,
                Quantity = line.Qty,
                UnitPrice = product.Price
            });
        }
        order.Total = order.Items.Sum(i => i.UnitPrice * i.Quantity);

        _db.Orders.Add(order);
        await _db.SaveChangesAsync();

        return Ok(new { orderId = order.Id });
    }
}