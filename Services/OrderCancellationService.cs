using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;
using ShopCoursework.Models;

namespace ShopCoursework.Services;

public enum OrderCancellationResult
{
    Cancelled,
    NotFound,
    NotCancellable
}

public sealed class OrderCancellationService(ApplicationDbContext db)
{
    public async Task<OrderCancellationResult> CancelAsync(int orderId, string? userId = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();

        var orderQuery = db.Orders.Where(order =>
            order.Id == orderId &&
            (order.Status == OrderStatuses.New || order.Status == OrderStatuses.Confirmed));
        if (userId != null)
            orderQuery = orderQuery.Where(order => order.UserId == userId);

        var updated = await orderQuery.ExecuteUpdateAsync(updates =>
            updates.SetProperty(order => order.Status, OrderStatuses.Cancelled));

        if (updated != 1)
        {
            await transaction.RollbackAsync();
            var existingOrder = db.Orders.Where(order => order.Id == orderId);
            if (userId != null)
                existingOrder = existingOrder.Where(order => order.UserId == userId);

            return await existingOrder.AnyAsync()
                ? OrderCancellationResult.NotCancellable
                : OrderCancellationResult.NotFound;
        }

        var order = await db.Orders
            .Where(candidate => candidate.Id == orderId)
            .Select(candidate => new { candidate.Status, candidate.PaymentStatus })
            .SingleAsync();
        db.OrderHistory.Add(new OrderHistoryEntry
        {
            OrderId = orderId,
            EventType = userId == null
                ? OrderHistoryEventTypes.AdminCancelled
                : OrderHistoryEventTypes.CustomerCancelled,
            Status = order.Status,
            PaymentStatus = order.PaymentStatus
        });

        var items = await db.OrderItems
            .Where(item => item.OrderId == orderId && item.ProductVariantId != null)
            .Select(item => new { VariantId = item.ProductVariantId!.Value, item.Quantity })
            .ToListAsync();

        foreach (var item in items.GroupBy(item => item.VariantId)
                     .Select(group => new { VariantId = group.Key, Quantity = group.Sum(item => item.Quantity) }))
        {
            var restored = await db.ProductVariants
                .Where(variant => variant.Id == item.VariantId)
                .ExecuteUpdateAsync(updates =>
                    updates.SetProperty(variant => variant.Stock, variant => variant.Stock + item.Quantity));

            if (restored != 1)
                throw new InvalidOperationException(
                    $"Cannot restore stock for variant {item.VariantId} while cancelling order {orderId}.");
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return OrderCancellationResult.Cancelled;
    }
}
