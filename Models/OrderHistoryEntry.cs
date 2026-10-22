namespace ShopCoursework.Models;

public static class OrderHistoryEventTypes
{
    public const string Created = "Заказ создан";
    public const string StatusChanged = "Статус заказа изменён";
    public const string PaymentReceived = "Получена оплата";
    public const string CustomerCancelled = "Заказ отменён покупателем";
    public const string AdminCancelled = "Заказ отменён администратором";
    public const string ExistingState = "Состояние на момент установки истории";
}

public sealed class OrderHistoryEntry
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public Order? Order { get; set; }
    public string EventType { get; set; } = "";
    public string Status { get; set; } = "";
    public string PaymentStatus { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
