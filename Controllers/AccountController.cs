using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using EstacionamientoWebApp.Services;

namespace EstacionamientoWebApp.Controllers
{
    public class AccountController : Controller
    {
        private readonly IAuthService _auth;
        public AccountController(IAuthService auth) => _auth = auth;

        [AllowAnonymous]                   // 👈 agregar
        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            ViewBag.ReturnUrl = returnUrl;
            return View();
        }

        [AllowAnonymous]                   // 👈 agregar
        [HttpPost]
        public async Task<IActionResult> Login(string username, string password, string? returnUrl = null)
        {
            var user = await _auth.ValidateUserAsync(username, password);
            if (user is null)
            {
                ModelState.AddModelError("", "Usuario o contraseña inválidos.");
                return View();
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user.DisplayName ?? user.Username),
                new(ClaimTypes.NameIdentifier, user.Username),
                new(TenantConnection.ConnClaim, user.ConnectionString),
                new("playa", user.Playa)
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            return Redirect(returnUrl ?? Url.Action("Index", "Ingreso")!);
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction("Login");
        }

        public IActionResult Denied() => View();
    }
}
