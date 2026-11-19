using EstacionamientoWebApp.Models;
using EstacionamientoWebApp.Services;
using EstacionamientoWebApp.Services.WSAA;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using QuestPDF;
using QuestPDF.Infrastructure;
using System.Net;
using System.Security.Authentication;

// ⚠️ CRÍTICO: Configurar TLS 1.2 para conexiones con AFIP/ARCA
// AFIP y ARCA exigen TLS 1.2 o superior en todas las conexiones SOAP/HTTPS
// Las versiones antiguas (TLS 1.0 y 1.1) están deshabilitadas y causarán errores SSL/TLS handshake
ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
ServicePointManager.ServerCertificateValidationCallback = null; // Usar validación por defecto

var builder = WebApplication.CreateBuilder(args);

// Configurar licencia de QuestPDF (Community - gratuita)
Settings.License = LicenseType.Community;

// (Opcional) Ver rápido el entorno y que exista AuthConnection
Console.WriteLine($"ENV={builder.Environment.EnvironmentName}");
Console.WriteLine($"AuthConnection configurada={(!string.IsNullOrEmpty(builder.Configuration.GetConnectionString("AuthConnection")))}");

// MVC
builder.Services.AddControllersWithViews();

// Cookie Auth
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/Login";
        // o.SlidingExpiration = true; // si querés
    });

// Dependencias
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ITenantConnection, TenantConnection>();
builder.Services.AddScoped<ITarifasService, TarifasService>();
builder.Services.AddTransient<WSAAService>();
builder.Services.AddScoped<AFIPService>();

// DbContext POR REQUEST con la connection string del claim
builder.Services.AddScoped<ApplicationDbContext>(sp =>
{
    var tenant = sp.GetRequiredService<ITenantConnection>();
    var options = new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseSqlServer(tenant.GetConnectionString(), sql => sql.EnableRetryOnFailure())
        .Options;

    return new ApplicationDbContext(options);
});

// ⚠️ No uses AddDbContext con una cadena fija.

var app = builder.Build();

// Pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Ruta por defecto: al Login para probar
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.Run();
