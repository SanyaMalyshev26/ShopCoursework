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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopCoursework.Data;
using ShopCoursework.Models;
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
    public async Task Readiness_endpoint_reports_database_availability()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"status":"ok"}""",
            await response.Content.ReadAsStringAsync());
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
    public async Task Sales_report_requires_admin_role()
    {
        await using var nonAdminFactory = new TestApplicationFactory($"Data Source={_databasePath}", isAdmin: false);
        using var nonAdminClient = nonAdminFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await nonAdminClient.GetAsync("/Admin/Analytics/Sales?from=2026-01-01&to=2026-01-01");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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

        var repeatedPayment = await PostFormAsync($"/Admin/Orders/{orderId}/MarkPaid");
        Assert.Equal(HttpStatusCode.OK, repeatedPayment.StatusCode);
        Assert.Equal(PaymentStatuses.Paid, await GetPaymentStatusAsync(orderId));

        var invalidCancel = await PostFormAsync($"/Admin/Orders/{orderId}/Cancel");
        Assert.Equal(HttpStatusCode.OK, invalidCancel.StatusCode);
        Assert.Equal(3, await GetStockAsync(variantId));
        Assert.Equal(OrderStatuses.Delivered, await GetOrderStatusAsync(orderId));

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
    }

    private async Task<int> CreateVariantAsync(int stock)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var variant = new ProductVariant
        {
            ProductId = 1,
            Sku = $"TEST-{Guid.NewGuid():N}",
            Size = "M",
            Color = "Чёрный",
            Stock = stock
        };
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();
        return variant.Id;
    }

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

    private sealed class TestApplicationFactory(string connectionString, bool isAdmin = true) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AdminEmail"] = "",
                    ["ConnectionStrings:DefaultConnection"] = connectionString
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IImageProcessingClient, TestImageProcessingClient>();
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<TestAuthenticationOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        options => options.IsAdmin = isAdmin);
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

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<TestAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<TestAuthenticationOptions>(options, logger, encoder)
    {
        public const string SchemeName = "OrderWorkflowTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
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
    }
}
