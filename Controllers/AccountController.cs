using Microsoft.AspNetCore.Mvc;

namespace ShopCoursework.Controllers;

public class AccountController : Controller
{
    public IActionResult Index()
    {
        return View();
    }
}