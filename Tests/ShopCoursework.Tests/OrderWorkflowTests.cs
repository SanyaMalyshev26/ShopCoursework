using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopCoursework.Data;
using ShopCoursework.Models;
using ShopCoursework.Services.Analytics;
using ShopCoursework.Services.Images;
using Xunit;

namespace ShopCoursework.Tests;

public sealed class OrderWorkflowTests : IAsyncLifetime
{
    private const string TestUserId = "inventory-workflow-test-user";
    private string _databasePath = "";
    private TestApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private string _antiforgeryToken = "";
    private int _addressId;

    public async Task InitializeAsync()
    {
        _databasePath = Path.Combine(Path.GetTempPath(), $"shopcoursework-tests-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={_databasePath}";
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connectionString)
            .Options;

        await using (var db = new ApplicationDbContext(dbOptions))
            await db.Database.MigrateAsync();

        _factory = new TestApplicationFactory(connectionString);
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true
        });

        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                Id = TestUserId,
                UserName = "workflow-test@example.invalid",
                Email = "workflow-test@example.invalid",
                FirstName = "Workflow",
                LastName = "Tester"
            };

            var result = await userManager.CreateAsync(user, "TestPass123");
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
        }

        _addressId = await CreateAddressAsync(TestUserId, "Дом", "Москва", "Тестовая, 1");
        _antiforgeryToken = await GetAntiforgeryTokenAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

        foreach (var suffix in new[] { "", "-shm", "-wal", "-journal" })
        {
            var path = _databasePath + suffix;
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task Cart_shows_the_users_saved_delivery_address()
    {
        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/Cart"));

        Assert.Contains("deliveryAddressId", html);
        Assert.Contains("Дом", html);
        Assert.Contains("Москва", html);
        Assert.Contains("Тестовая, 1", html);
        Assert.Contains(PaymentMethods.CashOnDelivery, html);
    }

    [Fact]
    public async Task Product_details_page_shows_description_variants_and_size_recommendation()
    {
        await CreateVariantAsync(stock: 3, size: "M");
        var inactiveVariantId = await CreateVariantAsync(stock: 5, size: "L");
        await CreateSizeGuideAsync("M", 90, 100, 70, 80, 90, 100);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await db.Products.SingleAsync(item => item.Id == 1);
            product.Description = "Описание для страницы товара";
            var inactiveVariant = await db.ProductVariants.SingleAsync(item => item.Id == inactiveVariantId);
            inactiveVariant.IsActive = false;
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/Products/1");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Описание для страницы товара", html);
        Assert.Contains("Подобрать размер по меркам", html);
        Assert.Contains("Таблица мерок модели", html);
        Assert.Contains("data-size=\"M\"", html);
        Assert.DoesNotContain($"value=\"{inactiveVariantId}\"", html);
        Assert.Contains("add-to-cart", html);

        var missing = await _client.GetAsync("/Products/999");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Catalog_links_to_product_details_and_carries_variant_size_data()
    {
        await CreateVariantAsync(stock: 2, size: "S");

        var html = WebUtility.HtmlDecode(await _client.GetStringAsync("/"));

        Assert.Contains("href=\"/Products/1\"", html);
        Assert.Contains("Подробнее о товаре", html);
        Assert.Contains("data-size=\"S\"", html);
        Assert.Contains("data-color=\"Чёрный\"", html);
    }

    [Fact]
    public async Task Catalog_filters_by_search_size_and_available_stock()
    {
        await CreateVariantAsync(stock: 2, size: "M");
        await CreateVariantAsync(stock: 0, size: "L");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await db.Products.SingleAsync(item => item.Id == 1);
            product.Description = "Особенная коллекция для поиска";
            await db.SaveChangesAsync();
        }

        var query = Uri.EscapeDataString("джин");

        var matchingResponse = await _client.GetAsync(
            $"/?search={query}&size=M&inStockOnly=true");
        var matchingHtml = WebUtility.HtmlDecode(await matchingResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, matchingResponse.StatusCode);
        Assert.Contains("href=\"/Products/1\"", matchingHtml);
        Assert.Contains("value=\"джин\"", matchingHtml);
        Assert.Contains("value=\"M\" selected", matchingHtml);
        Assert.Contains("checked", matchingHtml);
        Assert.DoesNotContain("href=\"/Products/2\"", matchingHtml);

        var descriptionSearch = await _client.GetAsync(
            $"/?search={Uri.EscapeDataString("ОСОБЕННАЯ")}");
        var descriptionHtml = WebUtility.HtmlDecode(await descriptionSearch.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, descriptionSearch.StatusCode);
        Assert.Contains("href=\"/Products/1\"", descriptionHtml);
        Assert.DoesNotContain("href=\"/Products/2\"", descriptionHtml);

        var unavailableResponse = await _client.GetAsync(
            $"/?search={query}&size=L&inStockOnly=true");
        var unavailableHtml = await unavailableResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, unavailableResponse.StatusCode);
        Assert.Contains("товары не найдены", WebUtility.HtmlDecode(unavailableHtml));
    }

    [Fact]
    public async Task Catalog_sorts_products_by_price_and_rejects_unknown_sort_options()
    {
        var sortedResponse = await _client.GetAsync("/?sort=price-desc");
        var sortedHtml = await sortedResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, sortedResponse.StatusCode);
        Assert.True(
            sortedHtml.IndexOf("href=\"/Products/2\"", StringComparison.Ordinal) <
            sortedHtml.IndexOf("href=\"/Products/1\"", StringComparison.Ordinal));

        var invalidSortResponse = await _client.GetAsync("/?sort=unexpected");
        Assert.Equal(HttpStatusCode.BadRequest, invalidSortResponse.StatusCode);
    }

    [Fact]
    public async Task Catalog_paginates_after_filters_and_preserves_filters_on_next_page()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            for (var index = 0; index < 9; index++)
            {
                db.Products.Add(new Product
                {
                    Name = $"Pagination-{index:D2}",
                    Price = 100 + index,
                    Rating = 4,
                    ImageUrl = "/test.webp"
                });
            }

            await db.SaveChangesAsync();
        }

        var firstPage = WebUtility.HtmlDecode(
            await _client.GetStringAsync("/?search=Pagination-"));
        Assert.Contains("Pagination-00", firstPage);
        Assert.Contains("Pagination-07", firstPage);
        Assert.DoesNotContain("Pagination-08", firstPage);
        Assert.Contains("Страница 1 из 2", firstPage);

        var secondPageResponse = await _client.GetAsync("/?search=Pagination-&page=2");
        var secondPage = WebUtility.HtmlDecode(await secondPageResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
        Assert.Contains("Pagination-08", secondPage);
        Assert.DoesNotContain("Pagination-07", secondPage);
        Assert.Contains("value=\"Pagination-\"", secondPage);
        Assert.Contains("Страница 2 из 2", secondPage);
        Assert.Contains("page=2", firstPage);

        var outOfRangePage = WebUtility.HtmlDecode(
            await _client.GetStringAsync("/?search=Pagination-&page=99"));
        Assert.Contains("Pagination-08", outOfRangePage);
        Assert.Contains("Страница 2 из 2", outOfRangePage);

        var invalidPage = await _client.GetAsync("/?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
    }

    [Fact]
    public async Task Admin_product_list_highlights_low_and_out_of_stock_active_variants()
    {
        var lowStockVariantId = await CreateVariantAsync(stock: 2, size: "M");
        var outOfStockVariantId = await CreateVariantAsync(stock: 0, size: "L");
        var healthyVariantId = await CreateVariantAsync(stock: 4, size: "XL");
        string lowStockSku;
        string outOfStockSku;
        string healthySku;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            lowStockSku = await db.ProductVariants
                .Where(variant => variant.Id == lowStockVariantId)
                .Select(variant => variant.Sku)
                .SingleAsync();
            outOfStockSku = await db.ProductVariants
                .Where(variant => variant.Id == outOfStockVariantId)
                .Select(variant => variant.Sku)
                .SingleAsync();
            var healthyVariant = await db.ProductVariants.SingleAsync(variant => variant.Id == healthyVariantId);
            healthySku = healthyVariant.Sku;
            healthyVariant.IsActive = false;
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/Admin/Products");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Низкий остаток (до 3 шт.):", html);
        Assert.Contains("Нет в наличии:", html);
        Assert.Contains("<strong>1</strong> SKU", html);
        Assert.Contains("<strong>1</strong> активных SKU", html);
        Assert.Contains($"{lowStockSku} (2)", html);
        Assert.Contains(outOfStockSku, html);
        Assert.DoesNotContain(healthySku, html);
    }

    [Fact]
    public async Task Admin_inventory_csv_exports_active_low_stock_variants_safely()
    {
        var lowStockVariantId = await CreateVariantAsync(stock: 2, size: "M");
        var outOfStockVariantId = await CreateVariantAsync(stock: 0, size: "S");
        var healthyVariantId = await CreateVariantAsync(stock: 4, size: "L");
        var inactiveVariantId = await CreateVariantAsync(stock: 1, size: "XL");
        string lowStockSku;
        string outOfStockSku;
        string healthySku;
        string inactiveSku;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var product = await db.Products.SingleAsync(item => item.Id == 1);
            product.Name = "  =HYPERLINK(\"https://example.invalid\",\"Click\")";
            var inactiveVariant = await db.ProductVariants.SingleAsync(item => item.Id == inactiveVariantId);
            inactiveVariant.IsActive = false;
            await db.SaveChangesAsync();

            lowStockSku = await db.ProductVariants.Where(item => item.Id == lowStockVariantId)
                .Select(item => item.Sku).SingleAsync();
            outOfStockSku = await db.ProductVariants.Where(item => item.Id == outOfStockVariantId)
                .Select(item => item.Sku).SingleAsync();
            healthySku = await db.ProductVariants.Where(item => item.Id == healthyVariantId)
                .Select(item => item.Sku).SingleAsync();
            inactiveSku = inactiveVariant.Sku;
        }

        var response = await _client.GetAsync("/Admin/Products/Inventory/Csv");
        var csv = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"ID товара\",\"Товар\",\"SKU\",\"Размер\",\"Цвет\",\"Остаток\",\"Порог\"", csv);
        Assert.Contains($"\"1\",\"'  =HYPERLINK(\"\"https://example.invalid\"\",\"\"Click\"\")\",\"{outOfStockSku}\",\"S\",\"Чёрный\",\"0\",\"3\"", csv);
        Assert.Contains($"\"1\",\"'  =HYPERLINK(\"\"https://example.invalid\"\",\"\"Click\"\")\",\"{lowStockSku}\",\"M\",\"Чёрный\",\"2\",\"3\"", csv);
        Assert.DoesNotContain(healthySku, csv);
        Assert.DoesNotContain(inactiveSku, csv);
    }

    [Fact]
    public async Task Readiness_endpoint_reports_database_availability()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"status":"ok"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Order_history_migration_backfills_existing_orders_with_current_state()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options;

        int orderId;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261022122521_AddStockNotifications");
            db.Users.Add(new ApplicationUser
            {
                Id = "history-migration-user",
                UserName = "history-migration@example.invalid",
                NormalizedUserName = "HISTORY-MIGRATION@EXAMPLE.INVALID",
                Email = "history-migration@example.invalid",
                NormalizedEmail = "HISTORY-MIGRATION@EXAMPLE.INVALID"
            });
            var order = new Order
            {
                UserId = "history-migration-user",
                Status = OrderStatuses.Confirmed,
                PaymentStatus = PaymentStatuses.Pending
            };
            db.Orders.Add(order);
            await db.SaveChangesAsync();
            orderId = order.Id;

            await db.Database.MigrateAsync();
            var history = await db.OrderHistory.SingleAsync(entry => entry.OrderId == orderId);
            Assert.Equal(OrderHistoryEventTypes.ExistingState, history.EventType);
            Assert.Equal(OrderStatuses.Confirmed, history.Status);
            Assert.Equal(PaymentStatuses.Pending, history.PaymentStatus);
            Assert.NotEqual(default, history.CreatedAt);
        }
    }

    [Fact]
    public async Task Admin_sales_report_aggregates_utc_period_and_excludes_cancelled_orders()
    {
        var variantId = await CreateVariantAsync(stock: 10);
        var firstOrderId = await CreateOrderAsync(variantId, quantity: 2);
        var lastOrderId = await CreateOrderAsync(variantId, quantity: 1);
        var cancelledOrderId = await CreateOrderAsync(variantId, quantity: 1);
        var previousOrderId = await CreateOrderAsync(variantId, quantity: 1);
        var nextOrderId = await CreateOrderAsync(variantId, quantity: 1);
        var reportDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var orders = await db.Orders
                .Include(order => order.Items)
                .Where(order => new[] { firstOrderId, lastOrderId, cancelledOrderId, previousOrderId, nextOrderId }
                    .Contains(order.Id))
                .ToDictionaryAsync(order => order.Id);

            orders[firstOrderId].CreatedAt = reportDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            orders[lastOrderId].CreatedAt = reportDate.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
            orders[lastOrderId].PaymentStatus = PaymentStatuses.Paid;
            orders[cancelledOrderId].CreatedAt = reportDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
            orders[cancelledOrderId].Status = OrderStatuses.Cancelled;
            orders[previousOrderId].CreatedAt = reportDate.AddDays(-1).ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc);
            orders[nextOrderId].CreatedAt = reportDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            orders[firstOrderId].Items.Single().ProductVariantId = null;
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync(
            $"/Admin/Analytics/Sales?from={reportDate:yyyy-MM-dd}&to={reportDate:yyyy-MM-dd}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var summary = root.GetProperty("summary");
        Assert.Equal(reportDate.ToString("yyyy-MM-dd"), root.GetProperty("from").GetString());
        Assert.Equal(reportDate.ToString("yyyy-MM-dd"), root.GetProperty("to").GetString());
        Assert.Equal("RUB", root.GetProperty("currencyCode").GetString());
        Assert.Equal(2, summary.GetProperty("orders").GetInt32());
        Assert.Equal(10500m, summary.GetProperty("revenue").GetDecimal());
        Assert.Equal(3500m, summary.GetProperty("paidRevenue").GetDecimal());
        Assert.Equal(3, summary.GetProperty("unitsSold").GetInt32());

        var daily = Assert.Single(root.GetProperty("daily").EnumerateArray());
        Assert.Equal(reportDate.ToString("yyyy-MM-dd"), daily.GetProperty("date").GetString());
        Assert.Equal(2, daily.GetProperty("orders").GetInt32());
        Assert.Equal(10500m, daily.GetProperty("revenue").GetDecimal());
        Assert.Equal(3, daily.GetProperty("unitsSold").GetInt32());

        var topProduct = Assert.Single(root.GetProperty("topProducts").EnumerateArray());
        Assert.Equal(1, topProduct.GetProperty("productId").GetInt32());
        Assert.Equal("Джинсы", topProduct.GetProperty("productName").GetString());
        Assert.Equal(3, topProduct.GetProperty("unitsSold").GetInt32());
        Assert.Equal(10500m, topProduct.GetProperty("revenue").GetDecimal());
        Assert.False(root.TryGetProperty("userId", out _));
        Assert.False(root.TryGetProperty("email", out _));
        Assert.False(root.TryGetProperty("shippingCity", out _));
    }

    [Fact]
    public async Task Admin_sales_report_returns_empty_aggregates_and_validates_period()
    {
        var response = await _client.GetAsync("/Admin/Analytics/Sales?from=2000-01-01&to=2000-01-01");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var summary = json.RootElement.GetProperty("summary");
        Assert.Equal(0, summary.GetProperty("orders").GetInt32());
        Assert.Equal(0m, summary.GetProperty("revenue").GetDecimal());
        Assert.Equal(0m, summary.GetProperty("paidRevenue").GetDecimal());
        Assert.Equal(0, summary.GetProperty("unitsSold").GetInt32());
        Assert.Empty(json.RootElement.GetProperty("daily").EnumerateArray());
        Assert.Empty(json.RootElement.GetProperty("topProducts").EnumerateArray());

        var invalid = await _client.GetAsync("/Admin/Analytics/Sales?from=2026-02-02&to=2026-02-01");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var missing = await _client.GetAsync("/Admin/Analytics/Sales?from=2026-02-01");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
    }

    [Fact]
    public async Task Admin_sales_csv_exports_summary_daily_and_top_products_safely()
    {
        var variantId = await CreateVariantAsync(stock: 2);
        var orderId = await CreateOrderAsync(variantId, quantity: 2);
        var reportDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        const string formulaName = "  =HYPERLINK(\"https://example.invalid\",\"Click\")";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var order = await db.Orders
                .Include(item => item.Items)
                .SingleAsync(item => item.Id == orderId);
            order.Items.Single().ProductName = formulaName;
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync(
            $"/Admin/Analytics/Sales/Csv?from={reportDate}&to={reportDate}");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var csv = Encoding.UTF8.GetString(bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Contains("sales-" + reportDate, response.Content.Headers.ContentDisposition?.FileName);
        Assert.StartsWith("\uFEFF", csv);
        Assert.Contains("\"Итог\",\"\",\"\",\"1\",\"7000.00\",\"0.00\",\"2\",\"\",\"RUB\"", csv);
        Assert.Contains($"\"День\",\"{reportDate}\",\"\",\"1\",\"7000.00\",\"\",\"2\",\"\",\"RUB\"", csv);
        Assert.Contains(
            "\"Товар\",\"\",\"'  =HYPERLINK(\"\"https://example.invalid\"\",\"\"Click\"\")\",\"\",\"7000.00\",\"\",\"2\",\"1\",\"RUB\"",
            csv);

        var invalidPeriod = await _client.GetAsync(
            "/Admin/Analytics/Sales/Csv?from=2026-02-02&to=2026-02-01");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPeriod.StatusCode);
    }

    [Fact]
    public async Task Admin_sales_page_displays_report_and_links_csv_for_selected_period()
    {
        var variantId = await CreateVariantAsync(stock: 2);
        await CreateOrderAsync(variantId, quantity: 1);
        var reportDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var response = await _client.GetAsync(
            $"/Admin/Analytics/Sales/Page?from={reportDate}&to={reportDate}");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Отчёт по продажам", html);
        Assert.Contains("Выручка", html);
        Assert.Contains("3\u00a0500,00", html);
        Assert.Contains("Джинсы", html);
        Assert.Contains("Дополнительная аналитика", html);
        Assert.Contains("Средний чек", html);
        Assert.Contains("Рост", html);
        Assert.Contains("Доля топ-10 товаров", html);
        Assert.Contains($"href=\"/Admin/Analytics/Sales/Csv?from={reportDate}&to={reportDate}\"", html);
        Assert.Contains($"value=\"{reportDate}\"", html);

        var analyticsClient = Assert.IsType<TestAnalyticsProcessingClient>(
            _factory.Services.GetRequiredService<IAnalyticsProcessingClient>());
        analyticsClient.IsUnavailable = true;
        var degradedResponse = await _client.GetAsync(
            $"/Admin/Analytics/Sales/Page?from={reportDate}&to={reportDate}");
        var degradedHtml = WebUtility.HtmlDecode(await degradedResponse.Content.ReadAsStringAsync());
        analyticsClient.IsUnavailable = false;
        Assert.Equal(HttpStatusCode.OK, degradedResponse.StatusCode);
        Assert.Contains("временно недоступны", degradedHtml);
        Assert.Contains("Отчёт по продажам", degradedHtml);

        var invalidPeriod = await _client.GetAsync(
            "/Admin/Analytics/Sales/Page?from=2026-02-02&to=2026-02-01");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPeriod.StatusCode);
    }

    [Fact]
    public async Task Admin_orders_can_be_filtered_by_status_payment_and_order_id()
    {
        var variantId = await CreateVariantAsync(stock: 3);
        var confirmedOrderId = await CreateOrderAsync(variantId, quantity: 1);
        var newOrderId = await CreateOrderAsync(variantId, quantity: 1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var confirmedOrder = await db.Orders.SingleAsync(order => order.Id == confirmedOrderId);
            confirmedOrder.Status = OrderStatuses.Confirmed;
            confirmedOrder.PaymentStatus = PaymentStatuses.Paid;
            await db.SaveChangesAsync();
        }

        var filter = Uri.EscapeDataString(OrderStatuses.Confirmed);
        var paymentFilter = Uri.EscapeDataString(PaymentStatuses.Paid);
        var response = await _client.GetAsync(
            $"/Admin/Orders?status={filter}&paymentStatus={paymentFilter}&search={confirmedOrderId}");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"#{confirmedOrderId}", html);
        Assert.DoesNotContain($"#{newOrderId}", html);
        Assert.Contains($"value=\"{OrderStatuses.Confirmed}\" selected", html);
        Assert.Contains($"value=\"{PaymentStatuses.Paid}\" selected", html);
        Assert.Contains($"value=\"{confirmedOrderId}\"", html);

        var invalidFilter = await _client.GetAsync("/Admin/Orders?status=Unknown");
        Assert.Equal(HttpStatusCode.BadRequest, invalidFilter.StatusCode);
    }

    [Fact]
    public async Task Admin_orders_are_paginated_and_keep_filters_between_pages()
    {
        var variantId = await CreateVariantAsync(stock: 12);
        var orderIds = new List<int>();
        for (var index = 0; index < 11; index++)
            orderIds.Add(await CreateOrderAsync(variantId, quantity: 1));

        var status = Uri.EscapeDataString(OrderStatuses.New);
        var payment = Uri.EscapeDataString(PaymentStatuses.Pending);
        var firstPageResponse = await _client.GetAsync(
            $"/Admin/Orders?status={status}&paymentStatus={payment}");
        var firstPage = WebUtility.HtmlDecode(await firstPageResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, firstPageResponse.StatusCode);
        Assert.Contains("Страница 1 из 2", firstPage);
        Assert.Contains($"<strong>#{orderIds[^1]}</strong>", firstPage);
        Assert.DoesNotContain($"<strong>#{orderIds[0]}</strong>", firstPage);

        var secondPageResponse = await _client.GetAsync(
            $"/Admin/Orders?status={status}&paymentStatus={payment}&page=2");
        var secondPage = WebUtility.HtmlDecode(await secondPageResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, secondPageResponse.StatusCode);
        Assert.Contains("Страница 2 из 2", secondPage);
        Assert.Contains($"<strong>#{orderIds[0]}</strong>", secondPage);
        Assert.Contains($"value=\"{OrderStatuses.New}\" selected", secondPage);
        Assert.Contains($"value=\"{PaymentStatuses.Pending}\" selected", secondPage);
        Assert.Contains("page=2", firstPage);

        var outOfRangePage = WebUtility.HtmlDecode(
            await _client.GetStringAsync($"/Admin/Orders?status={status}&paymentStatus={payment}&page=99"));
        Assert.Contains($"<strong>#{orderIds[0]}</strong>", outOfRangePage);
        Assert.Contains("Страница 2 из 2", outOfRangePage);

        var invalidPage = await _client.GetAsync("/Admin/Orders?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
    }

    [Fact]
    public async Task Size_recommendation_uses_all_entered_measurements_and_only_available_sizes()
    {
        await CreateVariantAsync(stock: 0, size: "S");
        await CreateVariantAsync(stock: 3, size: "M");
        await CreateVariantAsync(stock: 5, size: "L");
        await CreateSizeGuideAsync("S", 95, 105, 75, 85, 95, 105);
        await CreateSizeGuideAsync("M", 90, 100, 70, 80, 90, 100);
        await CreateSizeGuideAsync("L", 95, 105, 75, 85, 95, 105);

        using var response = await SendJsonAsync(
            "/Products/1/SizeRecommendation",
            new { chest = 100, waist = 80, hips = 100 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("L", json.RootElement.GetProperty("recommendedSize").GetString());
        Assert.Equal(5, json.RootElement.GetProperty("availableStock").GetInt32());
        var matchingSizes = json.RootElement.GetProperty("matchingSizes").EnumerateArray().ToArray();
        Assert.Equal(new[] { "L", "M" }, matchingSizes.Select(item => item.GetProperty("size").GetString()));
        Assert.Equal("Рекомендация ориентировочная. Проверьте таблицу размеров перед заказом.",
            json.RootElement.GetProperty("message").GetString());

        using var strictResponse = await SendJsonAsync(
            "/Products/1/SizeRecommendation",
            new { chest = 100, waist = 81 });
        using var strictJson = JsonDocument.Parse(await strictResponse.Content.ReadAsStringAsync());
        Assert.Equal("L", strictJson.RootElement.GetProperty("recommendedSize").GetString());
        Assert.Single(strictJson.RootElement.GetProperty("matchingSizes").EnumerateArray());

        using var noMatchResponse = await SendJsonAsync(
            "/Products/1/SizeRecommendation",
            new { chest = 60, waist = 81 });
        using var noMatchJson = JsonDocument.Parse(await noMatchResponse.Content.ReadAsStringAsync());
        Assert.Null(noMatchJson.RootElement.GetProperty("recommendedSize").GetString());
        Assert.Empty(noMatchJson.RootElement.GetProperty("matchingSizes").EnumerateArray());
    }

    [Fact]
    public async Task Size_recommendation_validates_measurements_and_handles_missing_size_chart()
    {
        using var noMeasurements = await SendJsonAsync(
            "/Products/1/SizeRecommendation",
            new { chest = (decimal?)null, waist = (decimal?)null, hips = (decimal?)null });
        Assert.Equal(HttpStatusCode.BadRequest, noMeasurements.StatusCode);

        using var outOfRange = await SendJsonAsync(
            "/Products/1/SizeRecommendation",
            new { chest = 201 });
        Assert.Equal(HttpStatusCode.BadRequest, outOfRange.StatusCode);

        using var missingProduct = await SendJsonAsync(
            "/Products/999/SizeRecommendation",
            new { waist = 80 });
        Assert.Equal(HttpStatusCode.NotFound, missingProduct.StatusCode);

        using var noChart = await SendJsonAsync(
            "/Products/1/SizeRecommendation",
            new { chest = 95 });
        Assert.Equal(HttpStatusCode.OK, noChart.StatusCode);
        using var json = JsonDocument.Parse(await noChart.Content.ReadAsStringAsync());
        Assert.Null(json.RootElement.GetProperty("recommendedSize").GetString());
        Assert.Equal("Таблица размеров для этой модели ещё не заполнена.",
            json.RootElement.GetProperty("message").GetString());

        await using var anonymousFactory = new TestApplicationFactory(
            $"Data Source={_databasePath}",
            isAdmin: false,
            isAuthenticated: false);
        using var anonymousClient = anonymousFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var anonymousResponse = await anonymousClient.PostAsync(
            "/Products/1/SizeRecommendation",
            new StringContent("""{"chest":95}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, anonymousResponse.StatusCode);
    }

    [Fact]
    public async Task Admin_can_create_update_and_delete_product_size_guides()
    {
        var create = await PostFormAsync("/Admin/Products/1/SizeGuides", SizeGuideForm("m"));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var guide = await db.ProductSizeGuides.SingleAsync();
            Assert.Equal("M", guide.Size);
            Assert.Equal(90m, guide.ChestMin);
            Assert.Equal(100m, guide.ChestMax);
        }

        var duplicate = await PostFormAsync("/Admin/Products/1/SizeGuides", SizeGuideForm("M"));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Single(await db.ProductSizeGuides.ToListAsync());
            var guide = await db.ProductSizeGuides.SingleAsync();
            var update = SizeGuideForm("L");
            update["ChestMin"] = "95";
            update["ChestMax"] = "105";
            var updateResponse = await PostFormAsync(
                $"/Admin/Products/1/SizeGuides/{guide.Id}/Update",
                update);
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var guide = await db.ProductSizeGuides.SingleAsync();
            Assert.Equal("L", guide.Size);
            Assert.Equal(95m, guide.ChestMin);
            var deleteResponse = await PostFormAsync(
                $"/Admin/Products/1/SizeGuides/{guide.Id}/Delete");
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        }

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.ProductSizeGuides.ToListAsync());
    }

    [Fact]
    public async Task Admin_cannot_save_invalid_or_reversed_size_measurements()
    {
        var invalid = SizeGuideForm("XL");
        invalid["ChestMin"] = "110";
        invalid["ChestMax"] = "90";
        var response = await PostFormAsync("/Admin/Products/1/SizeGuides", invalid);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.ProductSizeGuides.ToListAsync());
    }

    [Fact]
    public async Task Database_rejects_invalid_size_guide_ranges()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.ProductSizeGuides.Add(new ProductSizeGuide
        {
            ProductId = 1,
            Size = "INVALID",
            ChestMin = 110,
            ChestMax = 90,
            WaistMin = 70,
            WaistMax = 80,
            HipsMin = 90,
            HipsMax = 100
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Customer_can_subscribe_and_receive_one_account_notification_when_size_is_restocked()
    {
        var variantId = await CreateVariantAsync(stock: 0, size: "M");
        await CreateSizeGuideAsync("M", 90, 100, 70, 80, 90, 100);

        var productPage = WebUtility.HtmlDecode(await _client.GetStringAsync("/Products/1"));
        Assert.Contains("Сообщить о размере M", productPage);
        var subscribe = await PostFormAsync(
            "/Products/1/StockNotifications",
            new Dictionary<string, string> { ["size"] = " m " });
        Assert.Equal(HttpStatusCode.OK, subscribe.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var notification = await db.StockNotifications.SingleAsync();
            Assert.Equal(TestUserId, notification.UserId);
            Assert.Equal("M", notification.Size);
            Assert.Null(notification.NotifiedAt);
        }

        var duplicate = await PostFormAsync(
            "/Products/1/StockNotifications",
            new Dictionary<string, string> { ["size"] = "M" });
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Single(await db.StockNotifications.ToListAsync());
        }

        var accountBeforeRestock = WebUtility.HtmlDecode(await _client.GetStringAsync("/Account/Index#stock-notifications"));
        Assert.Contains("Ожидаем поступления", accountBeforeRestock);

        var restock = await PostFormAsync(
            $"/Admin/Products/1/Variants/{variantId}/Update",
            new Dictionary<string, string> { ["stock"] = "4", ["isActive"] = "true" });
        Assert.Equal(HttpStatusCode.OK, restock.StatusCode);
        DateTime notifiedAt;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            notifiedAt = (await db.StockNotifications.SingleAsync()).NotifiedAt!.Value;
        }

        Assert.True(notifiedAt.Kind is DateTimeKind.Utc or DateTimeKind.Unspecified);
        var accountAfterRestock = WebUtility.HtmlDecode(await _client.GetStringAsync("/Account/Index#stock-notifications"));
        Assert.Contains("Поступление было", accountAfterRestock);
        Assert.Contains("Сейчас в наличии: 4 шт.", accountAfterRestock);

        var sellOut = await PostFormAsync(
            $"/Admin/Products/1/Variants/{variantId}/Update",
            new Dictionary<string, string> { ["stock"] = "0", ["isActive"] = "true" });
        Assert.Equal(HttpStatusCode.OK, sellOut.StatusCode);
        var accountAfterSellOut = WebUtility.HtmlDecode(await _client.GetStringAsync("/Account/Index#stock-notifications"));
        Assert.Contains("Сейчас размер снова закончился", accountAfterSellOut);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Equal(notifiedAt, (await db.StockNotifications.SingleAsync()).NotifiedAt);
        }

        var remove = await PostFormAsync(
            "/Account/DeleteStockNotification",
            new Dictionary<string, string> { ["id"] = "1" });
        Assert.Equal(HttpStatusCode.OK, remove.StatusCode);
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await verifyDb.StockNotifications.ToListAsync());
    }

    [Fact]
    public async Task Stock_notification_requires_login_and_only_owner_can_remove_subscription()
    {
        await CreateVariantAsync(stock: 0, size: "M");
        await CreateSizeGuideAsync("M", 90, 100, 70, 80, 90, 100);
        await CreateUserAsync("another-stock-notification-user");
        var otherSubscriptionId = await CreateStockNotificationAsync("another-stock-notification-user");

        await using var anonymousFactory = new TestApplicationFactory(
            $"Data Source={_databasePath}",
            isAdmin: false,
            isAuthenticated: false);
        using var anonymousClient = anonymousFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var cartHtml = await anonymousClient.GetStringAsync("/Cart");
        var tokenMatch = Regex.Match(
            cartHtml,
            """<input(?=[^>]*name="__RequestVerificationToken")(?=[^>]*value="([^"]+)")[^>]*>""");
        Assert.True(tokenMatch.Success);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Products/1/StockNotifications")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["size"] = "M",
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(tokenMatch.Groups[1].Value)
            })
        };
        var anonymousResponse = await anonymousClient.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var removeOtherUsersSubscription = await PostFormAsync(
            "/Account/DeleteStockNotification",
            new Dictionary<string, string> { ["id"] = otherSubscriptionId.ToString() });
        Assert.Equal(HttpStatusCode.OK, removeOtherUsersSubscription.StatusCode);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var subscription = await verifyDb.StockNotifications.SingleAsync();
        Assert.Equal("another-stock-notification-user", subscription.UserId);
    }

    [Fact]
    public async Task Customer_cannot_subscribe_to_unknown_size_or_currently_available_size()
    {
        var variantId = await CreateVariantAsync(stock: 2, size: "M");
        await CreateSizeGuideAsync("M", 90, 100, 70, 80, 90, 100);

        var available = await PostFormAsync(
            "/Products/1/StockNotifications",
            new Dictionary<string, string> { ["size"] = "M" });
        var unknown = await PostFormAsync(
            "/Products/1/StockNotifications",
            new Dictionary<string, string> { ["size"] = "XL" });
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.StockNotifications.ToListAsync());
        Assert.Equal(2, await GetStockAsync(variantId));
    }

    [Fact]
    public async Task Adding_a_new_in_stock_variant_notifies_existing_size_subscribers()
    {
        await CreateSizeGuideAsync("XL", 105, 115, 85, 95, 105, 115);
        var subscribe = await PostFormAsync(
            "/Products/1/StockNotifications",
            new Dictionary<string, string> { ["size"] = "XL" });
        Assert.Equal(HttpStatusCode.OK, subscribe.StatusCode);

        var addVariant = await PostFormAsync(
            "/Admin/Products/1/Variants",
            new Dictionary<string, string>
            {
                ["Sku"] = "NOTIFY-NEW-STOCK-XL",
                ["Size"] = "XL",
                ["Color"] = "Синий",
                ["Stock"] = "2"
            });
        Assert.Equal(HttpStatusCode.OK, addVariant.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notification = await db.StockNotifications.SingleAsync();
        Assert.NotNull(notification.NotifiedAt);
        Assert.Equal(2, await db.ProductVariants
            .Where(variant => variant.ProductId == 1 && variant.Size == "XL")
            .SumAsync(variant => variant.Stock));
    }

    [Fact]
    public async Task Sales_report_requires_admin_role()
    {
        await using var nonAdminFactory = new TestApplicationFactory($"Data Source={_databasePath}", isAdmin: false);
        using var nonAdminClient = nonAdminFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await nonAdminClient.GetAsync("/Admin/Analytics/Sales?from=2026-01-01&to=2026-01-01");
        var insightsResponse = await nonAdminClient.GetAsync(
            "/Admin/Analytics/Sales/Insights?from=2026-01-01&to=2026-01-01");
        var csvResponse = await nonAdminClient.GetAsync(
            "/Admin/Analytics/Sales/Csv?from=2026-01-01&to=2026-01-01");
        var pageResponse = await nonAdminClient.GetAsync("/Admin/Analytics/Sales/Page");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, insightsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, csvResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, pageResponse.StatusCode);
    }

    [Fact]
    public async Task Sales_insights_sends_only_the_aggregated_report_to_python()
    {
        var variantId = await CreateVariantAsync(stock: 2);
        var checkout = await CheckoutAsync(variantId, quantity: 1);
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        var reportDate = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        var response = await _client.GetAsync(
            $"/Admin/Analytics/Sales/Insights?from={reportDate}&to={reportDate}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("RUB", json.RootElement.GetProperty("currencyCode").GetString());
        Assert.Equal("up", json.RootElement.GetProperty("revenueTrend").GetString());
        Assert.Equal("3500.00", json.RootElement.GetProperty("averageOrderValue").GetString());

        var client = _factory.Services.GetRequiredService<IAnalyticsProcessingClient>()
            as TestAnalyticsProcessingClient;
        Assert.NotNull(client);
        Assert.NotNull(client.LastReport);
        Assert.Equal(1, client.LastReport.Summary.Orders);
        Assert.Equal(3500m, client.LastReport.Summary.Revenue);
        Assert.Equal(1, Assert.Single(client.LastReport.Daily).UnitsSold);
        Assert.Equal("Джинсы", Assert.Single(client.LastReport.TopProducts).ProductName);
        var serializedReport = JsonSerializer.Serialize(client.LastReport, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("userId", serializedReport);
        Assert.DoesNotContain("email", serializedReport);
        Assert.DoesNotContain("shippingCity", serializedReport);
    }

    [Fact]
    public async Task Admin_image_upload_returns_processed_image_urls()
    {
        using var content = new MultipartFormDataContent();
        var image = new ByteArrayContent([1, 2, 3]);
        image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(image, "image", "product.png");
        content.Add(new StringContent(_antiforgeryToken), "__RequestVerificationToken");

        var response = await _client.PostAsync("/Admin/Products/Images", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("/uploads/test-image.webp", json.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal("/uploads/test-image_thumb.webp", json.RootElement.GetProperty("thumbnailUrl").GetString());
        Assert.Equal(640, json.RootElement.GetProperty("width").GetInt32());
    }

    [Fact]
    public async Task Admin_image_upload_rejects_unsupported_content_types()
    {
        using var content = new MultipartFormDataContent();
        var image = new ByteArrayContent([1, 2, 3]);
        image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/svg+xml");
        content.Add(image, "image", "product.svg");
        content.Add(new StringContent(_antiforgeryToken), "__RequestVerificationToken");

        var response = await _client.PostAsync("/Admin/Products/Images", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var client = _factory.Services.GetRequiredService<IImageProcessingClient>() as TestImageProcessingClient;
        Assert.NotNull(client);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Image_processing_client_posts_multipart_and_parses_service_response()
    {
        using var handler = new ImageProcessingResponseHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://image-processor:8000/")
        };
        var imageClient = new ImageProcessingClient(httpClient);
        await using var imageStream = new MemoryStream([1, 2, 3]);
        var image = new FormFile(imageStream, 0, imageStream.Length, "image", "product.png")
        {
            Headers = new HeaderDictionary { ["Content-Type"] = "image/png" }
        };

        var result = await imageClient.ProcessAsync(image, CancellationToken.None);

        Assert.Equal("/uploads/processed.webp", result.ImageUrl);
        Assert.Equal("/uploads/processed_thumb.webp", result.ThumbnailUrl);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri("http://image-processor:8000/images"), handler.RequestUri);
        Assert.Contains("name=image", handler.RequestBody);
        Assert.Contains("Content-Type: image/png", handler.RequestBody);
    }

    [Fact]
    public async Task Analytics_processing_client_posts_aggregates_and_parses_insights()
    {
        using var handler = new AnalyticsProcessingResponseHandler();
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://image-processor:8000/")
        };
        var analyticsClient = new AnalyticsProcessingClient(httpClient);
        var report = new SalesReport(
            "2026-10-19",
            "2026-10-19",
            "RUB",
            new SalesSummary(1, 3500m, 0m, 1),
            [new DailySales("2026-10-19", 1, 3500m, 1)],
            [new TopProductSales(1, "Джинсы", 1, 3500m)]);

        var result = await analyticsClient.AnalyzeSalesAsync(report, CancellationToken.None);

        Assert.Equal("3500.00", result.AverageOrderValue);
        Assert.Equal("insufficient_data", result.RevenueTrend);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(
            new Uri("http://image-processor:8000/analytics/sales-insights"),
            handler.RequestUri);
        Assert.Contains("\"currencyCode\":\"RUB\"", handler.RequestBody);
        Assert.Contains("\"from\":\"2026-10-19\"", handler.RequestBody);
        Assert.DoesNotContain("userId", handler.RequestBody);
        Assert.DoesNotContain("email", handler.RequestBody);
    }

    [Fact]
    public async Task Checkout_decrements_stock_and_saves_order_snapshots()
    {
        var variantId = await CreateVariantAsync(stock: 4);

        var response = await CheckoutAsync(variantId, quantity: 2);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var variant = await db.ProductVariants.AsNoTracking().SingleAsync(item => item.Id == variantId);
        var order = await db.Orders
            .Include(item => item.Items)
            .Include(item => item.History)
            .AsNoTracking()
            .SingleAsync();
        var line = Assert.Single(order.Items);

        Assert.Equal(2, variant.Stock);
        Assert.Equal(OrderStatuses.New, order.Status);
        Assert.Equal(PaymentMethods.CashOnDelivery, order.PaymentMethod);
        Assert.Equal(PaymentStatuses.Pending, order.PaymentStatus);
        Assert.Equal("Дом", order.ShippingAddressTitle);
        Assert.Equal("Москва", order.ShippingCity);
        Assert.Equal("Тестовая, 1", order.ShippingStreet);
        Assert.Equal("Джинсы", line.ProductName);
        Assert.StartsWith("TEST-", line.VariantSku);
        Assert.Equal("M", line.VariantSize);
        Assert.Equal("Чёрный", line.VariantColor);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(7000m, order.Total);
        var creationEvent = Assert.Single(order.History);
        Assert.Equal(OrderHistoryEventTypes.Created, creationEvent.EventType);
        Assert.Equal(OrderStatuses.New, creationEvent.Status);
        Assert.Equal(PaymentStatuses.Pending, creationEvent.PaymentStatus);
    }

    [Fact]
    public async Task Checkout_rejects_null_lines_without_changing_stock()
    {
        var variantId = await CreateVariantAsync(stock: 2);
        var response = await SendJsonAsync("/Orders/Create", new
        {
            addressId = _addressId,
            lines = new object?[] { null }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, await GetStockAsync(variantId));
    }

    [Fact]
    public async Task Checkout_rejects_insufficient_stock_without_creating_order()
    {
        var variantId = await CreateVariantAsync(stock: 1);

        var response = await CheckoutAsync(variantId, quantity: 2);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await db.ProductVariants.Where(item => item.Id == variantId).Select(item => item.Stock).SingleAsync());
        Assert.Empty(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task Checkout_retries_with_the_same_idempotency_key_return_one_order()
    {
        var variantId = await CreateVariantAsync(stock: 3);
        const string idempotencyKey = "checkout-retry-001";

        var first = await CheckoutAsync(variantId, quantity: 1, idempotencyKey: idempotencyKey);
        var retry = await CheckoutAsync(variantId, quantity: 1, idempotencyKey: idempotencyKey);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var retryJson = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
        Assert.False(firstJson.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.Equal(
            firstJson.RootElement.GetProperty("orderId").GetInt32(),
            retryJson.RootElement.GetProperty("orderId").GetInt32());
        Assert.True(retryJson.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.Equal(2, await GetStockAsync(variantId));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task Concurrent_checkout_retries_with_same_key_create_only_one_order()
    {
        var variantId = await CreateVariantAsync(stock: 2);
        const string idempotencyKey = "checkout-concurrent-001";

        var responses = await Task.WhenAll(
            CheckoutAsync(variantId, quantity: 1, idempotencyKey: idempotencyKey),
            CheckoutAsync(variantId, quantity: 1, idempotencyKey: idempotencyKey));

        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(responses, response =>
            Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));

        var retry = await CheckoutAsync(variantId, quantity: 1, idempotencyKey: idempotencyKey);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(1, await GetStockAsync(variantId));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Single(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task Checkout_rejects_reusing_an_idempotency_key_with_different_contents()
    {
        var firstVariantId = await CreateVariantAsync(stock: 3);
        var secondVariantId = await CreateVariantAsync(stock: 3);
        const string idempotencyKey = "checkout-payload-001";

        var first = await CheckoutAsync(firstVariantId, quantity: 1, idempotencyKey: idempotencyKey);
        var changed = await CheckoutAsync(secondVariantId, quantity: 1, idempotencyKey: idempotencyKey);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(2, await GetStockAsync(firstVariantId));
        Assert.Equal(3, await GetStockAsync(secondVariantId));
    }

    [Fact]
    public async Task Checkout_rejects_malformed_idempotency_key()
    {
        var variantId = await CreateVariantAsync(stock: 2);

        var response = await CheckoutAsync(variantId, quantity: 1, idempotencyKey: "short");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, await GetStockAsync(variantId));
    }

    [Fact]
    public async Task Admin_product_and_variant_validation_rejects_invalid_values()
    {
        var invalidProduct = await PostFormAsync("/Admin/Products/Create", new Dictionary<string, string>
        {
            ["Name"] = "Invalid test product",
            ["Price"] = "-1",
            ["Rating"] = "6",
            ["ImageUrl"] = ""
        });
        Assert.Equal(HttpStatusCode.OK, invalidProduct.StatusCode);

        var unsafeImageUrl = await PostFormAsync("/Admin/Products/Create", new Dictionary<string, string>
        {
            ["Name"] = "Unsafe image URL",
            ["Price"] = "100",
            ["Rating"] = "4",
            ["ImageUrl"] = "javascript:alert(1)"
        });
        Assert.Equal(HttpStatusCode.OK, unsafeImageUrl.StatusCode);

        var invalidVariant = await PostFormAsync("/Admin/Products/1/Variants", new Dictionary<string, string>
        {
            ["Sku"] = "INVALID-STOCK",
            ["Size"] = "M",
            ["Color"] = "Black",
            ["Stock"] = "-1"
        });
        Assert.Equal(HttpStatusCode.OK, invalidVariant.StatusCode);

        var oversizedSku = await PostFormAsync("/Admin/Products/1/Variants", new Dictionary<string, string>
        {
            ["Sku"] = new string('S', 65),
            ["Size"] = "M",
            ["Color"] = "Black",
            ["Stock"] = "1"
        });
        Assert.Equal(HttpStatusCode.OK, oversizedSku.StatusCode);

        var overpostedVariant = await PostFormAsync("/Admin/Products/Create", new Dictionary<string, string>
        {
            ["Id"] = "1000",
            ["Name"] = "Clean product",
            ["Price"] = "1250",
            ["Rating"] = "4",
            ["ImageUrl"] = "/images/clean-product.jpg",
            ["Variants[0].Sku"] = "OVERPOSTED-SKU",
            ["Variants[0].Size"] = "M",
            ["Variants[0].Color"] = "Black",
            ["Variants[0].Stock"] = "99"
        });
        Assert.Equal(HttpStatusCode.OK, overpostedVariant.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(5, await db.Products.CountAsync());
        Assert.Contains(await db.Products.ToListAsync(), product => product.Name == "Clean product");
        Assert.DoesNotContain(await db.Products.ToListAsync(), product => product.Id == 1000);
        var variants = await db.ProductVariants.ToListAsync();
        Assert.DoesNotContain(variants, variant => variant.Sku == "INVALID-STOCK");
        Assert.DoesNotContain(variants, variant => variant.Sku.Length > 64);
        Assert.DoesNotContain(variants, variant => variant.Sku == "OVERPOSTED-SKU");
    }

    [Fact]
    public async Task Account_rejects_oversized_profile_and_address_fields()
    {
        var invalidProfile = await PostFormAsync("/Account/UpdateProfile", new Dictionary<string, string>
        {
            ["firstName"] = new string('F', 101),
            ["lastName"] = "Tester",
            ["phone"] = new string('1', 33)
        });
        Assert.Equal(HttpStatusCode.OK, invalidProfile.StatusCode);

        var invalidAddress = await PostFormAsync("/Account/AddAddress", new Dictionary<string, string>
        {
            ["title"] = new string('T', 81),
            ["city"] = "Москва",
            ["street"] = "Тестовая, 1"
        });
        Assert.Equal(HttpStatusCode.OK, invalidAddress.StatusCode);

        var invalidPassword = await PostFormAsync("/Account/ChangePassword", new Dictionary<string, string>
        {
            ["currentPassword"] = "anything",
            ["newPassword"] = "short",
            ["confirmPassword"] = "short"
        });
        Assert.Equal(HttpStatusCode.OK, invalidPassword.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.SingleAsync(item => item.Id == TestUserId);
        Assert.Equal("Workflow", user.FirstName);
        Assert.Equal("Tester", user.LastName);
        Assert.Equal(1, await db.Addresses.CountAsync(item => item.UserId == TestUserId));
    }

    [Fact]
    public async Task Customer_can_cancel_own_new_order_and_stock_is_restored_once()
    {
        var variantId = await CreateVariantAsync(stock: 4);
        var checkout = await CheckoutAsync(variantId, quantity: 2);
        using var json = JsonDocument.Parse(await checkout.Content.ReadAsStringAsync());
        var orderId = json.RootElement.GetProperty("orderId").GetInt32();

        var accountHtml = WebUtility.HtmlDecode(await _client.GetStringAsync("/Account/Index"));
        Assert.Contains($"/Account/CancelOrder/{orderId}", accountHtml);
        Assert.Equal(2, await GetStockAsync(variantId));

        var cancel = await PostFormAsync($"/Account/CancelOrder/{orderId}");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(OrderStatuses.Cancelled, await GetOrderStatusAsync(orderId));
        Assert.Equal(4, await GetStockAsync(variantId));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var history = await db.OrderHistory
                .Where(entry => entry.OrderId == orderId)
                .OrderBy(entry => entry.Id)
                .ToListAsync();
            Assert.Equal(
                new[] { OrderHistoryEventTypes.Created, OrderHistoryEventTypes.CustomerCancelled },
                history.Select(entry => entry.EventType));
            Assert.Equal(OrderStatuses.Cancelled, history[^1].Status);
        }

        var repeatCancel = await PostFormAsync($"/Account/CancelOrder/{orderId}");
        Assert.Equal(HttpStatusCode.OK, repeatCancel.StatusCode);
        Assert.Equal(OrderStatuses.Cancelled, await GetOrderStatusAsync(orderId));
        Assert.Equal(4, await GetStockAsync(variantId));
    }

    [Fact]
    public async Task Customer_cannot_cancel_another_users_order()
    {
        var variantId = await CreateVariantAsync(stock: 3);
        var checkout = await CheckoutAsync(variantId, quantity: 1);
        using var json = JsonDocument.Parse(await checkout.Content.ReadAsStringAsync());
        var orderId = json.RootElement.GetProperty("orderId").GetInt32();
        const string anotherUserId = "another-workflow-test-user";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Users.Add(new ApplicationUser
            {
                Id = anotherUserId,
                UserName = "another-workflow-test@example.invalid",
                Email = "another-workflow-test@example.invalid"
            });
            await db.SaveChangesAsync();

            var order = await db.Orders.SingleAsync(item => item.Id == orderId);
            order.UserId = anotherUserId;
            await db.SaveChangesAsync();
        }

        var response = await PostFormAsync($"/Account/CancelOrder/{orderId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(OrderStatuses.New, await GetOrderStatusAsync(orderId));
        Assert.Equal(2, await GetStockAsync(variantId));
    }

    [Fact]
    public async Task Checkout_rejects_an_address_owned_by_another_user()
    {
        var variantId = await CreateVariantAsync(stock: 2);
        var otherAddressId = await CreateAddressAsync("another-user", "Чужой", "Казань", "Посторонняя, 2");

        var response = await CheckoutAsync(variantId, quantity: 1, otherAddressId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, await GetStockAsync(variantId));
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task Order_keeps_address_snapshot_after_saved_address_changes()
    {
        var variantId = await CreateVariantAsync(stock: 2);
        var response = await CheckoutAsync(variantId, quantity: 1);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var address = await db.Addresses.SingleAsync(item => item.Id == _addressId);
            address.City = "Санкт-Петербург";
            address.Street = "Новая, 99";
            await db.SaveChangesAsync();
        }

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await verifyDb.Orders.AsNoTracking().SingleAsync();
        Assert.Equal("Москва", order.ShippingCity);
        Assert.Equal("Тестовая, 1", order.ShippingStreet);

        var accountHtml = WebUtility.HtmlDecode(await _client.GetStringAsync("/Account/Index"));
        Assert.Contains("г. Москва, Тестовая, 1", accountHtml);
    }

    [Fact]
    public async Task Concurrent_checkouts_cannot_sell_the_last_unit_twice()
    {
        var variantId = await CreateVariantAsync(stock: 1);

        var responses = await Task.WhenAll(
            CheckoutAsync(variantId, quantity: 1),
            CheckoutAsync(variantId, quantity: 1));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await db.ProductVariants.Where(item => item.Id == variantId).Select(item => item.Stock).SingleAsync());
        Assert.Single(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task Admin_can_advance_order_and_cancellation_restores_stock_only_once()
    {
        var variantId = await CreateVariantAsync(stock: 5);
        var checkout = await CheckoutAsync(variantId, quantity: 2);
        Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);
        using var json = JsonDocument.Parse(await checkout.Content.ReadAsStringAsync());
        var orderId = json.RootElement.GetProperty("orderId").GetInt32();

        var confirm = await PostFormAsync($"/Admin/Orders/{orderId}/Confirm");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(OrderStatuses.Confirmed, await GetOrderStatusAsync(orderId));

        var ship = await PostFormAsync($"/Admin/Orders/{orderId}/Ship");
        Assert.Equal(HttpStatusCode.OK, ship.StatusCode);
        Assert.Equal(OrderStatuses.Shipped, await GetOrderStatusAsync(orderId));
        var accountAfterShipping = WebUtility.HtmlDecode(await _client.GetStringAsync("/Account/Index"));
        Assert.DoesNotContain($"/Account/CancelOrder/{orderId}", accountAfterShipping);
        var customerCancelAfterShipping = await PostFormAsync($"/Account/CancelOrder/{orderId}");
        Assert.Equal(HttpStatusCode.OK, customerCancelAfterShipping.StatusCode);
        Assert.Equal(OrderStatuses.Shipped, await GetOrderStatusAsync(orderId));
        Assert.Equal(3, await GetStockAsync(variantId));
        var adminHtml = WebUtility.HtmlDecode(await _client.GetStringAsync("/Admin/Orders"));
        Assert.Contains("г. Москва, Тестовая, 1", adminHtml);
        Assert.Contains(PaymentMethods.CashOnDelivery, adminHtml);

        var prematurePayment = await PostFormAsync($"/Admin/Orders/{orderId}/MarkPaid");
        Assert.Equal(HttpStatusCode.OK, prematurePayment.StatusCode);
        Assert.Equal(PaymentStatuses.Pending, await GetPaymentStatusAsync(orderId));

        var prematureCancel = await PostFormAsync($"/Admin/Orders/{orderId}/Cancel");
        Assert.Equal(HttpStatusCode.OK, prematureCancel.StatusCode);
        Assert.Equal(OrderStatuses.Shipped, await GetOrderStatusAsync(orderId));
        Assert.Equal(3, await GetStockAsync(variantId));

        var deliver = await PostFormAsync($"/Admin/Orders/{orderId}/Deliver");
        Assert.Equal(HttpStatusCode.OK, deliver.StatusCode);
        Assert.Equal(OrderStatuses.Delivered, await GetOrderStatusAsync(orderId));
        Assert.Equal(PaymentStatuses.Pending, await GetPaymentStatusAsync(orderId));

        var repeatedDelivery = await PostFormAsync($"/Admin/Orders/{orderId}/Deliver");
        Assert.Equal(HttpStatusCode.OK, repeatedDelivery.StatusCode);
        Assert.Equal(OrderStatuses.Delivered, await GetOrderStatusAsync(orderId));

        var markPaid = await PostFormAsync($"/Admin/Orders/{orderId}/MarkPaid");
        Assert.Equal(HttpStatusCode.OK, markPaid.StatusCode);
        Assert.Equal(PaymentStatuses.Paid, await GetPaymentStatusAsync(orderId));
        var accountHtml = WebUtility.HtmlDecode(await _client.GetStringAsync("/Account/Index"));
        Assert.Contains($"{OrderStatuses.Delivered}</span>", accountHtml);
        Assert.Contains($"{PaymentMethods.CashOnDelivery} · {PaymentStatuses.Paid}", accountHtml);
        Assert.Contains(OrderHistoryEventTypes.PaymentReceived, accountHtml);

        var repeatedPayment = await PostFormAsync($"/Admin/Orders/{orderId}/MarkPaid");
        Assert.Equal(HttpStatusCode.OK, repeatedPayment.StatusCode);
        Assert.Equal(PaymentStatuses.Paid, await GetPaymentStatusAsync(orderId));

        var invalidCancel = await PostFormAsync($"/Admin/Orders/{orderId}/Cancel");
        Assert.Equal(HttpStatusCode.OK, invalidCancel.StatusCode);
        Assert.Equal(3, await GetStockAsync(variantId));
        Assert.Equal(OrderStatuses.Delivered, await GetOrderStatusAsync(orderId));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var history = await db.OrderHistory
                .Where(entry => entry.OrderId == orderId)
                .OrderBy(entry => entry.Id)
                .ToListAsync();
            Assert.Equal(
                new[]
                {
                    OrderHistoryEventTypes.Created,
                    OrderHistoryEventTypes.StatusChanged,
                    OrderHistoryEventTypes.StatusChanged,
                    OrderHistoryEventTypes.StatusChanged,
                    OrderHistoryEventTypes.PaymentReceived
                },
                history.Select(entry => entry.EventType));
            Assert.Equal(OrderStatuses.Delivered, history[^1].Status);
            Assert.Equal(PaymentStatuses.Paid, history[^1].PaymentStatus);
        }
        Assert.Contains(OrderHistoryEventTypes.StatusChanged, adminHtml);

        var secondVariantId = await CreateVariantAsync(stock: 5);
        var secondCheckout = await CheckoutAsync(secondVariantId, quantity: 2);
        using var secondJson = JsonDocument.Parse(await secondCheckout.Content.ReadAsStringAsync());
        var cancellableOrderId = secondJson.RootElement.GetProperty("orderId").GetInt32();

        var cancel = await PostFormAsync($"/Admin/Orders/{cancellableOrderId}/Cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(OrderStatuses.Cancelled, await GetOrderStatusAsync(cancellableOrderId));
        Assert.Equal(5, await GetStockAsync(secondVariantId));

        var repeatCancel = await PostFormAsync($"/Admin/Orders/{cancellableOrderId}/Cancel");
        Assert.Equal(HttpStatusCode.OK, repeatCancel.StatusCode);
        Assert.Equal(OrderStatuses.Cancelled, await GetOrderStatusAsync(cancellableOrderId));
        Assert.Equal(5, await GetStockAsync(secondVariantId));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var history = await db.OrderHistory
                .Where(entry => entry.OrderId == cancellableOrderId)
                .OrderBy(entry => entry.Id)
                .ToListAsync();
            Assert.Equal(
                new[] { OrderHistoryEventTypes.Created, OrderHistoryEventTypes.AdminCancelled },
                history.Select(entry => entry.EventType));
        }
    }

    private async Task<int> CreateVariantAsync(int stock, string size = "M")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var variant = new ProductVariant
        {
            ProductId = 1,
            Sku = $"TEST-{Guid.NewGuid():N}",
            Size = size,
            Color = "Чёрный",
            Stock = stock
        };
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();
        return variant.Id;
    }

    private async Task<int> CreateSizeGuideAsync(
        string size,
        decimal chestMin,
        decimal chestMax,
        decimal waistMin,
        decimal waistMax,
        decimal hipsMin,
        decimal hipsMax)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var guide = new ProductSizeGuide
        {
            ProductId = 1,
            Size = size,
            ChestMin = chestMin,
            ChestMax = chestMax,
            WaistMin = waistMin,
            WaistMax = waistMax,
            HipsMin = hipsMin,
            HipsMax = hipsMax
        };
        db.ProductSizeGuides.Add(guide);
        await db.SaveChangesAsync();
        return guide.Id;
    }

    private static Dictionary<string, string> SizeGuideForm(string size) => new()
    {
        ["Size"] = size,
        ["ChestMin"] = "90",
        ["ChestMax"] = "100",
        ["WaistMin"] = "70",
        ["WaistMax"] = "80",
        ["HipsMin"] = "90",
        ["HipsMax"] = "100"
    };

    private async Task<int> CreateOrderAsync(int variantId, int quantity)
    {
        var response = await CheckoutAsync(variantId, quantity);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("orderId").GetInt32();
    }

    private async Task<int> CreateAddressAsync(string userId, string title, string city, string street)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var address = new Address
        {
            UserId = userId,
            Title = title,
            City = city,
            Street = street
        };
        db.Addresses.Add(address);
        await db.SaveChangesAsync();
        return address.Id;
    }

    private async Task CreateUserAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<ApplicationUser>>();
        var result = await userManager.CreateAsync(new ApplicationUser
        {
            Id = userId,
            UserName = $"{userId}@example.invalid",
            Email = $"{userId}@example.invalid",
            FirstName = "Other",
            LastName = "User"
        }, "TestPass123");
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(error => error.Description)));
    }

    private async Task<int> CreateStockNotificationAsync(string userId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notification = new StockNotification
        {
            UserId = userId,
            ProductId = 1,
            Size = "M"
        };
        db.StockNotifications.Add(notification);
        await db.SaveChangesAsync();
        return notification.Id;
    }

    private Task<HttpResponseMessage> CheckoutAsync(
        int variantId,
        int quantity,
        int? addressId = null,
        string? idempotencyKey = null) =>
        SendJsonAsync("/Orders/Create", new
        {
            addressId = addressId ?? _addressId,
            lines = new[] { new { variantId, qty = quantity, price = 0 } }
        }, idempotencyKey);

    private async Task<HttpResponseMessage> SendJsonAsync(string path, object body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RequestVerificationToken", _antiforgeryToken);
        if (idempotencyKey != null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> PostFormAsync(
        string path,
        Dictionary<string, string>? formValues = null)
    {
        var form = formValues ?? new Dictionary<string, string>();
        form["__RequestVerificationToken"] = _antiforgeryToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form)
        };
        return await _client.SendAsync(request);
    }

    private async Task<string> GetAntiforgeryTokenAsync()
    {
        var html = await _client.GetStringAsync("/Cart");
        var match = Regex.Match(
            html,
            """<input(?=[^>]*name="__RequestVerificationToken")(?=[^>]*value="([^"]+)")[^>]*>""");
        Assert.True(match.Success, "The cart page did not render an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private async Task<int> GetStockAsync(int variantId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ProductVariants.Where(item => item.Id == variantId).Select(item => item.Stock).SingleAsync();
    }

    private async Task<string> GetOrderStatusAsync(int orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Orders.Where(order => order.Id == orderId).Select(order => order.Status).SingleAsync();
    }

    private async Task<string> GetPaymentStatusAsync(int orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Orders.Where(order => order.Id == orderId).Select(order => order.PaymentStatus).SingleAsync();
    }

    private sealed class TestApplicationFactory(
        string connectionString,
        bool isAdmin = true,
        bool isAuthenticated = true) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AdminEmail"] = "",
                    ["ConnectionStrings:DefaultConnection"] = connectionString,
                    ["Inventory:LowStockThreshold"] = "3"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IImageProcessingClient, TestImageProcessingClient>();
                services.AddSingleton<IAnalyticsProcessingClient, TestAnalyticsProcessingClient>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<TestAuthenticationOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        options =>
                        {
                            options.IsAdmin = isAdmin;
                            options.IsAuthenticated = isAuthenticated;
                        });
            });
        }
    }

    private sealed class TestImageProcessingClient : IImageProcessingClient
    {
        public int Calls { get; private set; }

        public Task<ProcessedImage> ProcessAsync(IFormFile image, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ProcessedImage(
                "/uploads/test-image.webp",
                "/uploads/test-image_thumb.webp",
                640,
                480));
        }
    }

    private sealed class TestAnalyticsProcessingClient : IAnalyticsProcessingClient
    {
        public SalesReport? LastReport { get; private set; }
        public bool IsUnavailable { get; set; }

        public Task<SalesInsights> AnalyzeSalesAsync(
            SalesReport report,
            CancellationToken cancellationToken)
        {
            if (IsUnavailable)
                throw new HttpRequestException("Test analytics service is unavailable.");

            LastReport = report;
            return Task.FromResult(new SalesInsights(
                report.CurrencyCode,
                "3500.00",
                "3500.00",
                new BestSalesDay(report.From, "3500.00", 1, 1),
                "100.0",
                "up",
                "100.0"));
        }
    }

    private sealed class ImageProcessingResponseHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"imageUrl":"/uploads/processed.webp","thumbnailUrl":"/uploads/processed_thumb.webp","width":800,"height":600}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class AnalyticsProcessingResponseHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string RequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"currencyCode":"RUB","averageOrderValue":"3500.00","averageDailyRevenue":"3500.00","bestSalesDay":{"date":"2026-10-19","revenue":"3500.00","orders":1,"unitsSold":1},"revenueChangePercent":null,"revenueTrend":"insufficient_data","topProductsRevenueSharePercent":"100.0"}""",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<TestAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<TestAuthenticationOptions>(options, logger, encoder)
    {
        public const string SchemeName = "OrderWorkflowTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Options.IsAuthenticated)
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, TestUserId),
                new Claim(ClaimTypes.Name, "Workflow Tester")
            };
            if (Options.IsAdmin)
                claims.Add(new Claim(ClaimTypes.Role, "Admin"));
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    private sealed class TestAuthenticationOptions : AuthenticationSchemeOptions
    {
        public bool IsAdmin { get; set; }
        public bool IsAuthenticated { get; set; } = true;
    }
}
