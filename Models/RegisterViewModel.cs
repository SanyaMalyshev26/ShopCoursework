using System.ComponentModel.DataAnnotations;

namespace ShopCoursework.Models;

public class RegisterViewModel
{
    [Required(ErrorMessage = "Введите имя")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Введите фамилию")]
    public string LastName { get; set; } = "";

    [Required(ErrorMessage = "Введите email")]
    [EmailAddress(ErrorMessage = "Некорректный email")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Введите пароль")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [Compare("Password", ErrorMessage = "Пароли не совпадают")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = "";
}