namespace ShopCoursework.Models;

public sealed class AdminOrdersViewModel
{
    public IReadOnlyList<Order> Orders { get; init; } = Array.Empty<Order>();
    public string Status { get; init; } = "";
    public string PaymentStatus { get; init; } = "";
    public string Search { get; init; } = "";
    public int Page { get; init; }
    public int TotalPages { get; init; }

    public static IReadOnlyList<string> StatusOptions { get; } =
    [
        OrderStatuses.New,
        OrderStatuses.Confirmed,
        OrderStatuses.Shipped,
        OrderStatuses.Delivered,
        OrderStatuses.Cancelled
    ];

    public static IReadOnlyList<string> PaymentStatusOptions { get; } =
    [
        PaymentStatuses.Pending,
        PaymentStatuses.Paid
    ];
}
