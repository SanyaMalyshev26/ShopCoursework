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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>().Property(p => p.Price).HasPrecision(18, 2);

        modelBuilder.Entity<Product>().HasData(
            new Product { Id = 1, Name = "Джинсы",   Price = 3500,  Rating = 3.9 },
            new Product { Id = 2, Name = "Куртка",   Price = 12900, Rating = 4.1 },
            new Product { Id = 3, Name = "Футболка", Price = 1100,  Rating = 4.9 },
            new Product { Id = 4, Name = "Свитер",   Price = 2990,  Rating = 4.75 }
        );
    }
}