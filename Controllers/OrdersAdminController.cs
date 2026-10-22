using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;
using ShopCoursework.Models;
using ShopCoursework.Services.Csv;
using ShopCoursework.Services;

namespace ShopCoursework.Controllers;

[Authorize(Roles = "Admin")]
[Route("Admin/Orders")]
public class OrdersAdminController : Controller
{
    private const int PageSize = 10;
    private readonly ApplicationDbContext _db;
    private readonly OrderCancellationService _orderCancellation;

    public OrdersAdminController(ApplicationDbContext db, OrderCancellationService orderCancellation)
    {
        _db = db;
        _orderCancellation = orderCancellation;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        string? status,
        string? paymentStatus,
        string? search,
        int page = 1)
    {
        status = status?.Trim();
        paymentStatus = paymentStatus?.Trim();
        search = search?.Trim();
        if ((!string.IsNullOrEmpty(status) && !AdminOrdersViewModel.StatusOptions.Contains(status)) ||
            (!string.IsNullOrEmpty(paymentStatus) &&
             !AdminOrdersViewModel.PaymentStatusOptions.Contains(paymentStatus)) ||
            search?.Length > 120 ||
            page < 1)
        {
            return BadRequest();
        }

        var query = FilterOrders(_db.Orders.AsNoTracking(), status, paymentStatus, search);

        var totalCount = await query.CountAsync();
        var totalPages = (int)Math.Ceiling(totalCount / (double)PageSize);
        page = totalPages == 0 ? 1 : Math.Min(page, totalPages);
        var orders = await query
            .Include(order => order.User)
            .Include(order => order.Items)
            .Include(order => order.History.OrderBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id))
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .Skip((page - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync();

        return View(new AdminOrdersViewModel
        {
            Orders = orders,
            Status = status ?? "",
            PaymentStatus = paymentStatus ?? "",
            Search = search ?? "",
            Page = page,
            TotalPages = totalPages
        });
    }

    [HttpGet("Csv")]
    public async Task<IActionResult> Csv(
        string? status,
        string? paymentStatus,
        string? search,
        CancellationToken cancellationToken)
    {
        status = status?.Trim();
        paymentStatus = paymentStatus?.Trim();
        search = search?.Trim();
        if ((!string.IsNullOrEmpty(status) && !AdminOrdersViewModel.StatusOptions.Contains(status)) ||
            (!string.IsNullOrEmpty(paymentStatus) &&
             !AdminOrdersViewModel.PaymentStatusOptions.Contains(paymentStatus)) ||
            search?.Length > 120)
        {
            return BadRequest();
        }

        var orders = await FilterOrders(
                _db.Orders.AsNoTracking(),
                status,
                paymentStatus,
                search)
            .Include(order => order.User)
            .Include(order => order.Items)
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .ToListAsync(cancellationToken);

        var rows = new List<string>
        {
            CsvExport.Row(
                "Заказ",
                "Дата UTC",
                "Email",
                "Покупатель",
                "Статус",
                "Статус оплаты",
                "Способ оплаты",
                "Сумма",
                "Адрес доставки",
                "Состав")
        };
        rows.AddRange(orders.Select(order => CsvExport.Row(
            order.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            order.CreatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            order.User?.Email ?? "",
            $"{order.User?.FirstName} {order.User?.LastName}".Trim(),
            order.Status,
            order.PaymentStatus,
            order.PaymentMethod,
            order.Total.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            string.Join(", ", new[] { order.ShippingAddressTitle, order.ShippingCity, order.ShippingStreet }
                .Where(value => !string.IsNullOrWhiteSpace(value))),
            string.Join("; ", order.Items.Select(item =>
                $"{item.ProductName ?? item.Product?.Name ?? "Товар удалён"} " +
                $"{item.VariantSku} {item.VariantSize} {item.VariantColor} × {item.Quantity}")))));

        var csv = string.Join("\r\n", rows) + "\r\n";
        return File(
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv),
            "text/csv; charset=utf-8",
            $"orders-{DateTime.UtcNow:yyyy-MM-dd}.csv");
    }

    [HttpPost("{id:int}/Confirm")]
    public Task<IActionResult> Confirm(int id) =>
        ChangeStatus(id, OrderStatuses.New, OrderStatuses.Confirmed);

    [HttpPost("{id:int}/Ship")]
    public Task<IActionResult> Ship(int id) =>
        ChangeStatus(id, OrderStatuses.Confirmed, OrderStatuses.Shipped);

    [HttpPost("{id:int}/Deliver")]
    public Task<IActionResult> Deliver(int id) =>
        ChangeStatus(id, OrderStatuses.Shipped, OrderStatuses.Delivered);

    [HttpPost("{id:int}/MarkPaid")]
    public async Task<IActionResult> MarkPaid(int id)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        var updated = await _db.Orders
            .Where(order =>
                order.Id == id &&
                order.Status == OrderStatuses.Delivered &&
                order.PaymentMethod == PaymentMethods.CashOnDelivery &&
                order.PaymentStatus == PaymentStatuses.Pending)
            .ExecuteUpdateAsync(updates =>
                updates.SetProperty(order => order.PaymentStatus, PaymentStatuses.Paid));

        if (updated != 1)
        {
            await transaction.RollbackAsync();
            return await ReportFailedTransition(id);
        }

        var orderStatus = await _db.Orders
            .Where(order => order.Id == id)
            .Select(order => order.Status)
            .SingleAsync();
        _db.OrderHistory.Add(new OrderHistoryEntry
        {
            OrderId = id,
            EventType = OrderHistoryEventTypes.PaymentReceived,
            Status = orderStatus,
            PaymentStatus = PaymentStatuses.Paid
        });
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        TempData["Message"] = $"Для заказа #{id} отмечено получение оплаты.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:int}/Cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var result = await _orderCancellation.CancelAsync(id);
        if (result == OrderCancellationResult.NotFound)
            return NotFound();
        if (result == OrderCancellationResult.NotCancellable)
            return await ReportFailedTransition(id);

        TempData["Message"] = $"Заказ #{id} отменён, остатки возвращены на склад.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> ChangeStatus(int id, string currentStatus, string nextStatus)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        var updated = await _db.Orders
            .Where(order => order.Id == id && order.Status == currentStatus)
            .ExecuteUpdateAsync(updates => updates.SetProperty(order => order.Status, nextStatus));

        if (updated != 1)
        {
            await transaction.RollbackAsync();
            return await ReportFailedTransition(id);
        }

        var paymentStatus = await _db.Orders
            .Where(order => order.Id == id)
            .Select(order => order.PaymentStatus)
            .SingleAsync();
        _db.OrderHistory.Add(new OrderHistoryEntry
        {
            OrderId = id,
            EventType = OrderHistoryEventTypes.StatusChanged,
            Status = nextStatus,
            PaymentStatus = paymentStatus
        });
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        TempData["Message"] = $"Заказ #{id}: статус изменён на «{nextStatus}».";
        return RedirectToAction(nameof(Index));
    }

    private async Task<IActionResult> ReportFailedTransition(int id)
    {
        if (!await _db.Orders.AnyAsync(order => order.Id == id))
            return NotFound();

        TempData["Error"] = $"Статус заказа #{id} уже изменился или этот переход недопустим.";
        return RedirectToAction(nameof(Index));
    }

    private static IQueryable<Order> FilterOrders(
        IQueryable<Order> query,
        string? status,
        string? paymentStatus,
        string? search)
    {
        if (!string.IsNullOrEmpty(status))
            query = query.Where(order => order.Status == status);
        if (!string.IsNullOrEmpty(paymentStatus))
            query = query.Where(order => order.PaymentStatus == paymentStatus);
        if (!string.IsNullOrEmpty(search))
        {
            if (int.TryParse(search, out var orderId))
                query = query.Where(order =>
                    order.Id == orderId ||
                    (order.User != null && order.User.Email != null && order.User.Email.Contains(search)));
            else
                query = query.Where(order =>
                    order.User != null && order.User.Email != null && order.User.Email.Contains(search));
        }

        return query;
    }
}
