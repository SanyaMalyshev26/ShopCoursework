using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using ShopCoursework.Data;
using ShopCoursework.Models;
using ShopCoursework.Services;

namespace ShopCoursework.Controllers;

public class AccountController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ApplicationDbContext _db;
    private readonly OrderCancellationService _orderCancellation;

    public AccountController(UserManager<ApplicationUser> userManager,
                             SignInManager<ApplicationUser> signInManager,
                             ApplicationDbContext db,
                             OrderCancellationService orderCancellation)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _db = db;
        _orderCancellation = orderCancellation;
    }

    // Личный кабинет: только для вошедших
    [Authorize]
    public async Task<IActionResult> Index()
    {
        var user = await _userManager.GetUserAsync(User);
        ViewBag.Orders = await _db.Orders
            .Where(o => o.UserId == user!.Id)
            .Include(order => order.Items)
                .ThenInclude(item => item.Product)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
        ViewBag.Addresses = await _db.Addresses
            .Where(a => a.UserId == user!.Id)
            .OrderByDescending(a => a.IsDefault)
            .ThenBy(a => a.Id)
            .ToListAsync();
        return View(user);
    }

    [HttpGet]
    public IActionResult Register() => View();

    [HttpPost]
    public async Task<IActionResult> Register(RegisterViewModel model)
    {
        model.FirstName = model.FirstName?.Trim() ?? "";
        model.LastName = model.LastName?.Trim() ?? "";
        model.Email = model.Email?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(model.FirstName))
            ModelState.AddModelError(nameof(model.FirstName), "Введите имя");
        if (string.IsNullOrWhiteSpace(model.LastName))
            ModelState.AddModelError(nameof(model.LastName), "Введите фамилию");
        if (!ModelState.IsValid)
            return View(model);

        var user = new ApplicationUser
        {
            UserName = model.Email,
            Email = model.Email,
            FirstName = model.FirstName,
            LastName = model.LastName
        };

        var result = await _userManager.CreateAsync(user, model.Password);
        if (result.Succeeded)
        {
            await _signInManager.SignInAsync(user, isPersistent: false);
            return RedirectToAction("Index");
        }

        foreach (var error in result.Errors)
            ModelState.AddModelError("", error.Description);

        return View(model);
    }

    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!ModelState.IsValid)
            return View(model);

        var result = await _signInManager.PasswordSignInAsync(
            model.Email, model.Password, model.RememberMe, lockoutOnFailure: false);

        if (result.Succeeded)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
            return RedirectToAction("Index");
        }

        ModelState.AddModelError("", "Неверный email или пароль");
        return View(model);
    }

    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _signInManager.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> UpdateProfile(string? firstName, string? lastName, string? phone)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        firstName = firstName?.Trim();
        lastName = lastName?.Trim();
        phone = phone?.Trim();
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Length > 100 ||
            string.IsNullOrWhiteSpace(lastName) || lastName.Length > 100 ||
            phone?.Length > 32)
        {
            TempData["Error"] = "Имя и фамилия обязательны (до 100 символов), телефон — до 32 символов.";
            return RedirectToAction("Index");
        }

        user.FirstName = firstName;
        user.LastName = lastName;
        user.PhoneNumber = phone;

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            TempData["Error"] = string.Join(" ", result.Errors.Select(error => error.Description));
            return RedirectToAction("Index");
        }

        TempData["Message"] = "Данные профиля сохранены";
        return RedirectToAction("Index");
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> ChangePassword(
        string? currentPassword,
        string? newPassword,
        string? confirmPassword)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        if (string.IsNullOrEmpty(currentPassword) ||
            string.IsNullOrEmpty(newPassword) ||
            newPassword.Length is < 6 or > 100 ||
            confirmPassword is null)
        {
            TempData["Error"] = "Введите текущий пароль и новый пароль длиной от 6 до 100 символов.";
            return RedirectToAction("Index");
        }

        if (newPassword != confirmPassword)
        {
            TempData["Error"] = "Пароли не совпадают";
            return RedirectToAction("Index");
        }

        var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded)
        {
            await _signInManager.RefreshSignInAsync(user);
            TempData["Message"] = "Пароль изменён";
        }
        else
        {
            TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
        }
        return RedirectToAction("Index");
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> AddAddress(string? title, string? city, string? street)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        title = title?.Trim();
        city = city?.Trim();
        street = street?.Trim();
        if (string.IsNullOrWhiteSpace(city) || city.Length > 100 ||
            string.IsNullOrWhiteSpace(street) || street.Length > 200 ||
            title?.Length > 80)
        {
            TempData["Error"] = "Укажите город (до 100 символов) и улицу (до 200 символов); название адреса — до 80 символов.";
            return RedirectToAction("Index");
        }

        var hasAny = await _db.Addresses.AnyAsync(a => a.UserId == user.Id);

        _db.Addresses.Add(new Address
        {
            UserId = user.Id,
            Title = string.IsNullOrWhiteSpace(title) ? "Адрес" : title,
            City = city,
            Street = street,
            IsDefault = !hasAny
        });
        await _db.SaveChangesAsync();

        TempData["Message"] = "Адрес добавлен";
        return RedirectToAction("Index");
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> DeleteAddress(int id)
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var address = await _db.Addresses
            .FirstOrDefaultAsync(a => a.Id == id && a.UserId == user.Id);

        if (address != null)
        {
            var wasDefault = address.IsDefault;
            _db.Addresses.Remove(address);
            await _db.SaveChangesAsync();

            if (wasDefault)
            {
                var next = await _db.Addresses
                    .Where(a => a.UserId == user.Id)
                    .OrderBy(a => a.Id)
                    .FirstOrDefaultAsync();

                if (next != null)
                {
                    next.IsDefault = true;
                    await _db.SaveChangesAsync();
                }
            }

            TempData["Message"] = "Адрес удалён";
        }
        return RedirectToAction("Index");
    }

    [Authorize]
    [HttpPost]
    public async Task<IActionResult> CancelOrder(int id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var result = await _orderCancellation.CancelAsync(id, userId);
        if (result == OrderCancellationResult.Cancelled)
            TempData["Message"] = $"Заказ #{id} отменён, остатки возвращены на склад.";
        else
            TempData["Error"] = "Заказ не найден или уже передан в доставку и не может быть отменён.";

        return Redirect("/Account/Index#orders");
    }
}