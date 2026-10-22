using Microsoft.EntityFrameworkCore;
using ShopCoursework.Data;

namespace ShopCoursework.Services.StockNotifications;

public sealed class StockNotificationService(ApplicationDbContext db)
{
    public async Task NotifyAvailableSizeAsync(
        int productId,
        string size,
        CancellationToken cancellationToken = default)
    {
        var normalizedSize = size.Trim().ToUpperInvariant();
        var availableStock = await db.ProductVariants
            .Where(variant =>
                variant.ProductId == productId &&
                variant.IsActive &&
                variant.Stock > 0 &&
                variant.Size.ToUpper() == normalizedSize)
            .SumAsync(variant => (int?)variant.Stock, cancellationToken) ?? 0;
        if (availableStock == 0)
            return;

        var now = DateTime.UtcNow;
        await db.StockNotifications
            .Where(notification =>
                notification.ProductId == productId &&
                notification.Size == normalizedSize &&
                notification.NotifiedAt == null)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(notification => notification.NotifiedAt, now),
                cancellationToken);
    }
}
