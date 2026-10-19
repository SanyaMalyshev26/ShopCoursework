using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;
using ShopCoursework.Models;

namespace ShopCoursework.Controllers;

public class CartLine
{
    public int VariantId { get; set; }
    public int Qty { get; set; }
}

public class CheckoutRequest
{
    public int AddressId { get; set; }
    public List<CartLine>? Lines { get; set; }
}

public class OrdersController : Controller
{
    private readonly ApplicationDbContext _db;

    public OrdersController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CheckoutRequest? request)
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized();

        if (request?.Lines == null || request.Lines.Count == 0 || request.Lines.Count > 100 ||
            request.Lines.Any(line => line.VariantId < 1 || line.Qty is < 1 or > 99))
        {
            return BadRequest(new { message = "Корзина содержит некорректные данные." });
        }

        if (request.AddressId < 1)
            return BadRequest(new { message = "Выберите адрес доставки." });

        var quantities = request.Lines
            .GroupBy(line => line.VariantId)
            .Select(group => new
            {
                VariantId = group.Key,
                Quantity = group.Sum(line => line.Qty)
            })
            .ToList();

        if (quantities.Any(line => line.Quantity > 99))
            return BadRequest();

        await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

        var address = await _db.Addresses
            .Where(candidate =>
                candidate.Id == request.AddressId &&
                candidate.UserId == User.FindFirstValue(ClaimTypes.NameIdentifier))
            .Select(candidate => new
            {
                candidate.Title,
                candidate.City,
                candidate.Street
            })
            .SingleOrDefaultAsync();

        if (address == null)
        {
            await transaction.RollbackAsync();
            return BadRequest(new { message = "Выбранный адрес доставки недоступен. Обновите страницу." });
        }

        var variantIds = quantities.Select(line => line.VariantId).ToList();
        var variants = await _db.ProductVariants
            .Include(variant => variant.Product)
            .Where(variant => variantIds.Contains(variant.Id) && variant.IsActive)
            .ToDictionaryAsync(variant => variant.Id);

        if (variants.Count != variantIds.Count ||
            quantities.Any(line => variants[line.VariantId].Stock < line.Quantity))
        {
            await transaction.RollbackAsync();
            return Conflict(new { message = "Один из выбранных вариантов закончился или недоступен." });
        }

        var order = new Order
        {
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier)!,
            PaymentMethod = PaymentMethods.CashOnDelivery,
            ShippingAddressTitle = address.Title,
            ShippingCity = address.City,
            ShippingStreet = address.Street
        };

        foreach (var line in quantities)
        {
            var variant = variants[line.VariantId];
            var reserved = await _db.ProductVariants
                .Where(candidate =>
                    candidate.Id == line.VariantId &&
                    candidate.IsActive &&
                    candidate.Stock >= line.Quantity)
                .ExecuteUpdateAsync(updates =>
                    updates.SetProperty(candidate => candidate.Stock, candidate => candidate.Stock - line.Quantity));

            if (reserved != 1)
            {
                await transaction.RollbackAsync();
                return Conflict(new { message = "Один из выбранных вариантов только что закончился." });
            }

            var product = variant.Product!;
            order.Items.Add(new OrderItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                ProductVariantId = variant.Id,
                VariantSku = variant.Sku,
                VariantSize = variant.Size,
                VariantColor = variant.Color,
                Quantity = line.Quantity,
                UnitPrice = product.Price
            });
        }

        order.Total = order.Items.Sum(item => item.UnitPrice * item.Quantity);
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new { orderId = order.Id });
    }
}
