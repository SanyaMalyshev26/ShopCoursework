using System.Globalization;
using System.Text.Json;

namespace ShopCoursework.Services.Analytics;

public sealed record SalesSummary(int Orders, decimal Revenue, decimal PaidRevenue, int UnitsSold);

public sealed record DailySales(string Date, int Orders, decimal Revenue, int UnitsSold);

public sealed record TopProductSales(int ProductId, string ProductName, int UnitsSold, decimal Revenue);

public sealed record SalesReport(
    string From,
    string To,
    string CurrencyCode,
    SalesSummary Summary,
    IReadOnlyList<DailySales> Daily,
    IReadOnlyList<TopProductSales> TopProducts);

public sealed record BestSalesDay(string Date, string Revenue, int Orders, int UnitsSold);

public sealed record SalesInsights(
    string CurrencyCode,
    string? AverageOrderValue,
    string AverageDailyRevenue,
    BestSalesDay? BestSalesDay,
    string? RevenueChangePercent,
    string RevenueTrend,
    string? TopProductsRevenueSharePercent);

public sealed class AnalyticsProcessingException(
    string message,
    Exception? innerException = null)
    : Exception(message, innerException);

public interface IAnalyticsProcessingClient
{
    Task<SalesInsights> AnalyzeSalesAsync(SalesReport report, CancellationToken cancellationToken);
}

public sealed class AnalyticsProcessingClient(HttpClient httpClient) : IAnalyticsProcessingClient
{
    public async Task<SalesInsights> AnalyzeSalesAsync(
        SalesReport report,
        CancellationToken cancellationToken)
    {
        if (httpClient.BaseAddress == null)
            throw new AnalyticsProcessingException("Sales analytics service is not configured.");

        using var response = await httpClient.PostAsJsonAsync(
            "analytics/sales-insights",
            report,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AnalyticsProcessingException("Sales analytics service rejected the report.");

        try
        {
            var insights = await response.Content.ReadFromJsonAsync<SalesInsights>(
                cancellationToken: cancellationToken);
            if (insights == null || !IsValidResponse(insights, report.CurrencyCode))
                throw new AnalyticsProcessingException(
                    "Sales analytics service returned an invalid response.");

            return insights;
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new AnalyticsProcessingException(
                "Sales analytics service returned invalid JSON.",
                exception);
        }
    }

    private static bool IsValidResponse(SalesInsights insights, string currencyCode)
    {
        var validTrend = insights.RevenueTrend is
            "up" or "down" or "unchanged" or "new_sales" or "insufficient_data";
        return insights.CurrencyCode == currencyCode &&
            validTrend &&
            IsNonNegativeAmount(insights.AverageOrderValue, allowNull: true) &&
            IsNonNegativeAmount(insights.AverageDailyRevenue) &&
            IsDecimal(insights.RevenueChangePercent, allowNull: true) &&
            IsPercentage(insights.TopProductsRevenueSharePercent) &&
            (insights.BestSalesDay == null ||
                (DateOnly.TryParseExact(
                     insights.BestSalesDay.Date,
                     "yyyy-MM-dd",
                     CultureInfo.InvariantCulture,
                     DateTimeStyles.None,
                     out _) &&
                 IsNonNegativeAmount(insights.BestSalesDay.Revenue) &&
                 insights.BestSalesDay.Orders >= 0 &&
                 insights.BestSalesDay.UnitsSold >= 0));
    }

    private static bool IsNonNegativeAmount(string? value, bool allowNull = false) =>
        (allowNull && value == null) ||
        (IsDecimal(value, allowNull: false) &&
         decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) &&
         amount >= 0);

    private static bool IsPercentage(string? value) =>
        value == null ||
        (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var percentage) &&
         percentage is >= 0 and <= 100);

    private static bool IsDecimal(string? value, bool allowNull) =>
        (allowNull && value == null) ||
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
}
