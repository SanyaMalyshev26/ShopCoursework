using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ShopCoursework.Models;

namespace ShopCoursework.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ProductSizeGuide> ProductSizeGuides => Set<ProductSizeGuide>();
    public DbSet<StockNotification> StockNotifications => Set<StockNotification>();

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderHistoryEntry> OrderHistory => Set<OrderHistoryEntry>();

    public DbSet<Address> Addresses => Set<Address>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>().Property(p => p.Price).HasPrecision(18, 2);
        modelBuilder.Entity<Order>().Property(o => o.Total).HasPrecision(18, 2);
        modelBuilder.Entity<OrderItem>().Property(i => i.UnitPrice).HasPrecision(18, 2);
        modelBuilder.Entity<Order>()
            .HasIndex(order => new { order.UserId, order.CheckoutRequestId })
            .IsUnique();
        modelBuilder.Entity<Order>()
            .Property(order => order.CheckoutRequestId)
            .HasMaxLength(128);
        modelBuilder.Entity<Order>()
            .Property(order => order.CheckoutRequestHash)
            .HasMaxLength(64);
        modelBuilder.Entity<OrderHistoryEntry>()
            .Property(entry => entry.EventType)
            .HasMaxLength(64);
        modelBuilder.Entity<OrderHistoryEntry>()
            .Property(entry => entry.Status)
            .HasMaxLength(32);
        modelBuilder.Entity<OrderHistoryEntry>()
            .Property(entry => entry.PaymentStatus)
            .HasMaxLength(32);
        modelBuilder.Entity<OrderHistoryEntry>()
            .HasIndex(entry => new { entry.OrderId, entry.CreatedAt, entry.Id });
        modelBuilder.Entity<OrderHistoryEntry>()
            .HasOne(entry => entry.Order)
            .WithMany(order => order.History)
            .HasForeignKey(entry => entry.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ProductVariant>()
            .HasIndex(v => v.Sku)
            .IsUnique();
        modelBuilder.Entity<ProductVariant>()
            .Property(v => v.Sku)
            .HasMaxLength(64);
        modelBuilder.Entity<ProductVariant>()
            .Property(v => v.Size)
            .HasMaxLength(32);
        modelBuilder.Entity<ProductVariant>()
            .Property(v => v.Color)
            .HasMaxLength(64);
        modelBuilder.Entity<ProductVariant>()
            .ToTable(table => table.HasCheckConstraint("CK_ProductVariants_Stock_NonNegative", "\"Stock\" >= 0"));
        modelBuilder.Entity<ProductVariant>()
            .HasOne(v => v.Product)
            .WithMany(p => p.Variants)
            .HasForeignKey(v => v.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ProductSizeGuide>()
            .HasIndex(guide => new { guide.ProductId, guide.Size })
            .IsUnique();
        modelBuilder.Entity<ProductSizeGuide>()
            .ToTable(table =>
            {
                table.HasCheckConstraint(
                    "CK_ProductSizeGuides_ChestRange",
                    "CAST(\"ChestMin\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"ChestMax\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"ChestMin\" AS REAL) <= CAST(\"ChestMax\" AS REAL)");
                table.HasCheckConstraint(
                    "CK_ProductSizeGuides_WaistRange",
                    "CAST(\"WaistMin\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"WaistMax\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"WaistMin\" AS REAL) <= CAST(\"WaistMax\" AS REAL)");
                table.HasCheckConstraint(
                    "CK_ProductSizeGuides_HipsRange",
                    "CAST(\"HipsMin\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"HipsMax\" AS REAL) BETWEEN 30 AND 200 AND CAST(\"HipsMin\" AS REAL) <= CAST(\"HipsMax\" AS REAL)");
            });
        modelBuilder.Entity<ProductSizeGuide>()
            .HasOne(guide => guide.Product)
            .WithMany(product => product.SizeGuides)
            .HasForeignKey(guide => guide.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StockNotification>()
            .HasIndex(notification => new
            {
                notification.UserId,
                notification.ProductId,
                notification.Size
            })
            .IsUnique();
        modelBuilder.Entity<StockNotification>()
            .HasOne(notification => notification.User)
            .WithMany()
            .HasForeignKey(notification => notification.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<StockNotification>()
            .HasOne(notification => notification.Product)
            .WithMany()
            .HasForeignKey(notification => notification.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OrderItem>()
            .HasOne(i => i.ProductVariant)
            .WithMany()
            .HasForeignKey(i => i.ProductVariantId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Product>().HasData(
            new Product { Id = 1, Name = "Джинсы",   Price = 3500,  Rating = 3.9 },
            new Product { Id = 2, Name = "Куртка",   Price = 12900, Rating = 4.1 },
            new Product { Id = 3, Name = "Футболка", Price = 1100,  Rating = 4.9 },
            new Product { Id = 4, Name = "Свитер",   Price = 2990,  Rating = 4.75 }
        );
    }
}