namespace ShopCoursework.Models;

public class Address
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "";
    public string City { get; set; } = "";
    public string Street { get; set; } = "";
    public bool IsDefault { get; set; }
}