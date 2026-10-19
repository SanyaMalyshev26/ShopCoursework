using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ShopCoursework.Data;
using ShopCoursework.Models;

namespace ShopCoursework.Controllers;

[Authorize(Roles = "Admin")]
[Route("Admin/Analytics")]
public class AnalyticsAdminController : Controller
{
    private const int MaximumPeriodDays = 366;
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _configuration;

    public AnalyticsAdminController(ApplicationDbContext db, IConfiguration configuration)
    {
        _db = db;
        _configuration = configuration;
    }

    [HttpGet("Sales")]
    public async Task<IActionResult> Sales(
        [FromQuery] string? from,
        [FromQuery] string? to,
        CancellationToken cancellationToken)
    {
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate) ||
            !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate))
        {
            return BadRequest(new { message = "Укажите даты from и to в формате YYYY-MM-DD." });
        }

        if (endDate < startDate ||
            endDate.DayNumber - startDate.DayNumber + 1 > MaximumPeriodDays ||
            endDate == DateOnly.MaxValue)
        {
            return BadRequest(new { message = "Период должен быть не более 366 дней, а дата to — не раньше from." });
        }

        var start = DateTime.SpecifyKind(startDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var endExclusive = DateTime.SpecifyKind(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

        var orders = _db.Orders
            .AsNoTracking()
            .Where(order =>
                order.CreatedAt >= start &&
                order.CreatedAt < endExclusive &&
                order.Status != OrderStatuses.Cancelled);

        var orderAmounts = await orders
            .Select(order => new { order.Total, order.PaymentStatus })
            .ToListAsync(cancellationToken);
        var orderCount = orderAmounts.Count;
        var revenue = orderAmounts.Sum(order => order.Total);
        var paidRevenue = orderAmounts
            .Where(order => order.PaymentStatus == PaymentStatuses.Paid)
            .Sum(order => order.Total);
        var unitsSold = await _db.OrderItems
            .Where(item =>
                item.Order != null &&
                item.Order.CreatedAt >= start &&
                item.Order.CreatedAt < endExclusive &&
                item.Order.Status != OrderStatuses.Cancelled)
            .SumAsync(item => (int?)item.Quantity, cancellationToken) ?? 0;

        var dailyOrderRows = await orders
            .Select(order => new { order.CreatedAt, order.Total })
            .ToListAsync(cancellationToken);
        var dailyOrders = dailyOrderRows
            .GroupBy(order => order.CreatedAt.Date)
            .Select(group => new
            {
                Date = group.Key,
                Orders = group.Count(),
                Revenue = group.Sum(order => order.Total)
            })
            .ToList();

        var dailyUnits = await _db.OrderItems
            .Where(item =>
                item.Order != null &&
                item.Order.CreatedAt >= start &&
                item.Order.CreatedAt < endExclusive &&
                item.Order.Status != OrderStatuses.Cancelled)
            .GroupBy(item => item.Order!.CreatedAt.Date)
            .Select(group => new { Date = group.Key, UnitsSold = group.Sum(item => item.Quantity) })
            .ToDictionaryAsync(group => group.Date, group => group.UnitsSold, cancellationToken);

        var productUnits = await _db.OrderItems
            .Where(item =>
                item.Order != null &&
                item.Order.CreatedAt >= start &&
                item.Order.CreatedAt < endExclusive &&
                item.Order.Status != OrderStatuses.Cancelled)
            .GroupBy(item => new { item.ProductId, item.ProductName })
            .Select(group => new
            {
                group.Key.ProductId,
                group.Key.ProductName,
                UnitsSold = group.Sum(item => item.Quantity),
            })
            .OrderByDescending(product => product.UnitsSold)
            .ThenBy(product => product.ProductId)
            .ThenBy(product => product.ProductName)
            .Take(10)
            .ToListAsync(cancellationToken);
        var topProductIds = productUnits.Select(product => product.ProductId).Distinct().ToArray();
        var topProductAmounts = await _db.OrderItems
            .Where(item =>
                item.Order != null &&
                item.Order.CreatedAt >= start &&
                item.Order.CreatedAt < endExclusive &&
                item.Order.Status != OrderStatuses.Cancelled &&
                topProductIds.Contains(item.ProductId))
            .Select(item => new { item.ProductId, item.ProductName, item.UnitPrice, item.Quantity })
            .ToListAsync(cancellationToken);
        var productRevenue = topProductAmounts
            .GroupBy(item => new { item.ProductId, item.ProductName })
            .ToDictionary(
                group => (group.Key.ProductId, group.Key.ProductName),
                group => group.Sum(item => item.UnitPrice * item.Quantity));

        return Ok(new
        {
            from = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            currencyCode = _configuration["Store:CurrencyCode"],
            summary = new { orders = orderCount, revenue, paidRevenue, unitsSold },
            daily = dailyOrders
                .OrderBy(day => day.Date)
                .Select(day => new
                {
                    date = day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    orders = day.Orders,
                    revenue = day.Revenue,
                    unitsSold = dailyUnits.GetValueOrDefault(day.Date)
                }),
            topProducts = productUnits.Select(product => new
            {
                productId = product.ProductId,
                productName = product.ProductName ?? $"Товар №{product.ProductId}",
                unitsSold = product.UnitsSold,
                revenue = productRevenue.GetValueOrDefault((product.ProductId, product.ProductName))
            })
        });
    }
}
