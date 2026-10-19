using System.ComponentModel.DataAnnotations;

namespace ShopCoursework.Models;

public class RegisterViewModel
{
    [Required(ErrorMessage = "Введите имя")]
    [StringLength(100, ErrorMessage = "Имя не должно превышать 100 символов")]
    public string FirstName { get; set; } = "";

    [Required(ErrorMessage = "Введите фамилию")]
    [StringLength(100, ErrorMessage = "Фамилия не должна превышать 100 символов")]
    public string LastName { get; set; } = "";

    [Required(ErrorMessage = "Введите email")]
    [EmailAddress(ErrorMessage = "Некорректный email")]
    [StringLength(256, ErrorMessage = "Email не должен превышать 256 символов")]
    public string Email { get; set; } = "";

    [Required(ErrorMessage = "Введите пароль")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "Пароль должен содержать от 6 до 100 символов")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = "";

    [StringLength(100, ErrorMessage = "Пароль не должен превышать 100 символов")]
    [Compare("Password", ErrorMessage = "Пароли не совпадают")]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = "";
}