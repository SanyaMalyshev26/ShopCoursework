using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopCoursework.Data;
using ShopCoursework.Models;
using Xunit;

namespace ShopCoursework.Tests;

public sealed class OrderWorkflowTests : IAsyncLifetime
{
    private const string TestUserId = "inventory-workflow-test-user";
    private string _databasePath = "";
    private TestApplicationFactory _factory = null!;
    private HttpClient _client = null!;
    private string _antiforgeryToken = "";

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
        Assert.Equal("Джинсы", line.ProductName);
        Assert.StartsWith("TEST-", line.VariantSku);
        Assert.Equal("M", line.VariantSize);
        Assert.Equal("Чёрный", line.VariantColor);
        Assert.Equal(2, line.Quantity);
        Assert.Equal(7000m, order.Total);
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

        var confirm = await AdminPostAsync($"/Admin/Orders/{orderId}/Confirm");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        Assert.Equal(OrderStatuses.Confirmed, await GetOrderStatusAsync(orderId));

        var ship = await AdminPostAsync($"/Admin/Orders/{orderId}/Ship");
        Assert.Equal(HttpStatusCode.OK, ship.StatusCode);
        Assert.Equal(OrderStatuses.Shipped, await GetOrderStatusAsync(orderId));

        var invalidCancel = await AdminPostAsync($"/Admin/Orders/{orderId}/Cancel");
        Assert.Equal(HttpStatusCode.OK, invalidCancel.StatusCode);
        Assert.Equal(3, await GetStockAsync(variantId));
        Assert.Equal(OrderStatuses.Shipped, await GetOrderStatusAsync(orderId));

        var secondVariantId = await CreateVariantAsync(stock: 5);
        var secondCheckout = await CheckoutAsync(secondVariantId, quantity: 2);
        using var secondJson = JsonDocument.Parse(await secondCheckout.Content.ReadAsStringAsync());
        var cancellableOrderId = secondJson.RootElement.GetProperty("orderId").GetInt32();

        var cancel = await AdminPostAsync($"/Admin/Orders/{cancellableOrderId}/Cancel");
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(OrderStatuses.Cancelled, await GetOrderStatusAsync(cancellableOrderId));
        Assert.Equal(5, await GetStockAsync(secondVariantId));

        var repeatCancel = await AdminPostAsync($"/Admin/Orders/{cancellableOrderId}/Cancel");
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

    private Task<HttpResponseMessage> CheckoutAsync(int variantId, int quantity) =>
        SendJsonAsync("/Orders/Create", new[] { new { variantId, qty = quantity } });

    private async Task<HttpResponseMessage> SendJsonAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("RequestVerificationToken", _antiforgeryToken);
        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> AdminPostAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = _antiforgeryToken
            })
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

    private sealed class TestApplicationFactory(string connectionString) : WebApplicationFactory<Program>
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
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
            });
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "OrderWorkflowTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, TestUserId),
                new Claim(ClaimTypes.Name, "Workflow Tester"),
                new Claim(ClaimTypes.Role, "Admin")
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
