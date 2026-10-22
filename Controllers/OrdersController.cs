using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
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
    public List<CartLine?>? Lines { get; set; }
}

public sealed record CheckoutQuantity(int VariantId, int Quantity);

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
            request.Lines.Any(line => line == null || line.VariantId < 1 || line.Qty is < 1 or > 99))
        {
            return BadRequest(new { message = "Корзина содержит некорректные данные." });
        }

        if (request.AddressId < 1)
            return BadRequest(new { message = "Выберите адрес доставки." });

        var quantities = request.Lines
            .OfType<CartLine>()
            .GroupBy(line => line.VariantId)
            .Select(group => new CheckoutQuantity(group.Key, group.Sum(line => line.Qty)))
            .ToList();

        if (quantities.Any(line => line.Quantity > 99))
            return BadRequest(new { message = "Количество одного варианта не может превышать 99." });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        string? idempotencyKey = null;
        if (Request.Headers.TryGetValue("Idempotency-Key", out var headerValues))
        {
            if (headerValues.Count != 1 ||
                headerValues[0] is not { Length: >= 16 and <= 128 } headerValue ||
                headerValue.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
            {
                return BadRequest(new { message = "Заголовок Idempotency-Key должен содержать 16–128 латинских букв, цифр, точек, дефисов или подчёркиваний." });
            }

            idempotencyKey = headerValue;
        }

        var requestHash = CreateRequestHash(request.AddressId, quantities);
        if (idempotencyKey != null)
        {
            var existing = await _db.Orders
                .AsNoTracking()
                .Where(order => order.UserId == userId && order.CheckoutRequestId == idempotencyKey)
                .Select(order => new { order.Id, order.CheckoutRequestHash })
                .SingleOrDefaultAsync();

            if (existing != null)
                return IdempotencyResponse(existing.Id, existing.CheckoutRequestHash, requestHash);
        }

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            if (idempotencyKey != null)
            {
                var existing = await _db.Orders
                    .AsNoTracking()
                    .Where(order => order.UserId == userId && order.CheckoutRequestId == idempotencyKey)
                    .Select(order => new { order.Id, order.CheckoutRequestHash })
                    .SingleOrDefaultAsync();

                if (existing != null)
                {
                    await transaction.RollbackAsync();
                    return IdempotencyResponse(existing.Id, existing.CheckoutRequestHash, requestHash);
                }
            }

            var address = await _db.Addresses
                .Where(candidate => candidate.Id == request.AddressId && candidate.UserId == userId)
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
                UserId = userId,
                PaymentMethod = PaymentMethods.CashOnDelivery,
                CheckoutRequestId = idempotencyKey,
                CheckoutRequestHash = idempotencyKey == null ? null : requestHash,
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
            order.History.Add(new OrderHistoryEntry
            {
                EventType = OrderHistoryEventTypes.Created,
                Status = order.Status,
                PaymentStatus = order.PaymentStatus
            });
            _db.Orders.Add(order);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(new { orderId = order.Id, duplicate = false });
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Conflict(new { message = "Оформление заказа выполняется параллельно. Повторите запрос; для защиты от дубля используйте тот же Idempotency-Key." });
        }
        catch (DbUpdateException exception) when (
            idempotencyKey != null &&
            exception.GetBaseException() is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            var existing = await _db.Orders
                .AsNoTracking()
                .Where(order => order.UserId == userId && order.CheckoutRequestId == idempotencyKey)
                .Select(order => new { order.Id, order.CheckoutRequestHash })
                .SingleOrDefaultAsync();

            if (existing == null)
                throw;

            return IdempotencyResponse(existing.Id, existing.CheckoutRequestHash, requestHash);
        }
    }

    private static string CreateRequestHash(
        int addressId,
        IEnumerable<CheckoutQuantity> quantities)
    {
        var lines = string.Join(
            ',',
            quantities.OrderBy(line => line.VariantId).Select(line =>
                $"{line.VariantId.ToString(CultureInfo.InvariantCulture)}:{line.Quantity.ToString(CultureInfo.InvariantCulture)}"));
        var payload = $"{addressId.ToString(CultureInfo.InvariantCulture)}|{lines}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private IActionResult IdempotencyResponse(int orderId, string? storedHash, string requestHash)
    {
        if (!string.Equals(storedHash, requestHash, StringComparison.Ordinal))
            return Conflict(new { message = "Idempotency-Key уже использован с другим составом заказа." });

        return Ok(new { orderId, duplicate = true });
    }
}
