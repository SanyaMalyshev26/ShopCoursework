using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;
using ShopCoursework.Models;
using ShopCoursework.Services;

namespace ShopCoursework.Controllers;

[Authorize(Roles = "Admin")]
[Route("Admin/Orders")]
public class OrdersAdminController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly OrderCancellationService _orderCancellation;

    public OrdersAdminController(ApplicationDbContext db, OrderCancellationService orderCancellation)
    {
        _db = db;
        _orderCancellation = orderCancellation;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var orders = await _db.Orders
            .Include(order => order.User)
            .Include(order => order.Items)
            .OrderByDescending(order => order.CreatedAt)
            .ThenByDescending(order => order.Id)
            .ToListAsync();

        return View(orders);
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
        var updated = await _db.Orders
            .Where(order =>
                order.Id == id &&
                order.Status == OrderStatuses.Delivered &&
                order.PaymentMethod == PaymentMethods.CashOnDelivery &&
                order.PaymentStatus == PaymentStatuses.Pending)
            .ExecuteUpdateAsync(updates =>
                updates.SetProperty(order => order.PaymentStatus, PaymentStatuses.Paid));

        if (updated != 1)
            return await ReportFailedTransition(id);

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
        var updated = await _db.Orders
            .Where(order => order.Id == id && order.Status == currentStatus)
            .ExecuteUpdateAsync(updates => updates.SetProperty(order => order.Status, nextStatus));

        if (updated != 1)
            return await ReportFailedTransition(id);

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
}
