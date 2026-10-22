using ShopCoursework.Services.Analytics;

namespace ShopCoursework.Models;

public sealed record SalesDashboardViewModel(
    SalesReport Report,
    SalesInsights? Insights,
    string? InsightsError);
