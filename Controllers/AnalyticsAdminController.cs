using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ShopCoursework.Data;
using ShopCoursework.Models;
using ShopCoursework.Services.Analytics;

namespace ShopCoursework.Controllers;

[Authorize(Roles = "Admin")]
[Route("Admin/Analytics")]
public class AnalyticsAdminController : Controller
{
    private const int MaximumPeriodDays = 366;
    private readonly ApplicationDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IAnalyticsProcessingClient _analyticsClient;
    private readonly ILogger<AnalyticsAdminController> _logger;

    public AnalyticsAdminController(
        ApplicationDbContext db,
        IConfiguration configuration,
        IAnalyticsProcessingClient analyticsClient,
        ILogger<AnalyticsAdminController> logger)
    {
        _db = db;
        _configuration = configuration;
        _analyticsClient = analyticsClient;
        _logger = logger;
    }

    [HttpGet("Sales")]
    public async Task<IActionResult> Sales(
        [FromQuery] string? from,
        [FromQuery] string? to,
        CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(from, to, out var startDate, out var endDate, out var error))
            return BadRequest(new { message = error });

        return Ok(await BuildReportAsync(startDate, endDate, cancellationToken));
    }

    [HttpGet("Sales/Insights")]
    public async Task<IActionResult> SalesInsightsReport(
        [FromQuery] string? from,
        [FromQuery] string? to,
        CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(from, to, out var startDate, out var endDate, out var error))
            return BadRequest(new { message = error });

        var report = await BuildReportAsync(startDate, endDate, cancellationToken);
        try
        {
            return Ok(await _analyticsClient.AnalyzeSalesAsync(report, cancellationToken));
        }
        catch (AnalyticsProcessingException exception)
        {
            _logger.LogWarning(exception, "The sales analytics processor rejected or returned an invalid report.");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { message = "Сервис аналитики временно недоступен." });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogWarning(exception, "The sales analytics processor is unavailable.");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { message = "Сервис аналитики временно недоступен." });
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "The sales analytics processor timed out.");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                new { message = "Сервис аналитики временно недоступен." });
        }
    }

    private async Task<SalesReport> BuildReportAsync(
        DateOnly startDate,
        DateOnly endDate,
        CancellationToken cancellationToken)
    {
        var start = DateTime.SpecifyKind(startDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var endExclusive = DateTime.SpecifyKind(endDate.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var orders = _db.Orders
            .AsNoTracking()
            .Where(order =>
                order.CreatedAt >= start &&
                order.CreatedAt < endExclusive &&
                order.Status != OrderStatuses.Cancelled);

        var orderRows = await orders
            .Select(order => new { order.CreatedAt, order.Total, order.PaymentStatus })
            .ToListAsync(cancellationToken);
        var revenue = orderRows.Sum(order => order.Total);
        var paidRevenue = orderRows
            .Where(order => order.PaymentStatus == PaymentStatuses.Paid)
            .Sum(order => order.Total);
        var unitsSold = await _db.OrderItems
            .Where(item =>
                item.Order != null &&
                item.Order.CreatedAt >= start &&
                item.Order.CreatedAt < endExclusive &&
                item.Order.Status != OrderStatuses.Cancelled)
            .SumAsync(item => (int?)item.Quantity, cancellationToken) ?? 0;

        var dailyOrders = orderRows
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

        return new SalesReport(
            startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _configuration["Store:CurrencyCode"] ?? "RUB",
            new SalesSummary(orderRows.Count, revenue, paidRevenue, unitsSold),
            dailyOrders
                .OrderBy(day => day.Date)
                .Select(day => new DailySales(
                    day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    day.Orders,
                    day.Revenue,
                    dailyUnits.GetValueOrDefault(day.Date)))
                .ToList(),
            productUnits.Select(product => new TopProductSales(
                product.ProductId,
                product.ProductName ?? $"Товар №{product.ProductId}",
                product.UnitsSold,
                productRevenue.GetValueOrDefault((product.ProductId, product.ProductName))))
                .ToList());
    }

    private static bool TryParsePeriod(
        string? from,
        string? to,
        out DateOnly startDate,
        out DateOnly endDate,
        out string error)
    {
        var validStart = DateOnly.TryParseExact(
            from,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out startDate);
        var validEnd = DateOnly.TryParseExact(
            to,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out endDate);
        if (!validStart || !validEnd)
        {
            error = "Укажите даты from и to в формате YYYY-MM-DD.";
            return false;
        }

        if (endDate < startDate ||
            endDate.DayNumber - startDate.DayNumber + 1 > MaximumPeriodDays ||
            endDate == DateOnly.MaxValue)
        {
            error = "Период должен быть не более 366 дней, а дата to — не раньше from.";
            return false;
        }

        error = "";
        return true;
    }
}
