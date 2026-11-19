using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace EstacionamientoWebApp.Services
{
    public class TenantConnection : ITenantConnection
    {
        public const string ConnClaim = "conn";
        private readonly IHttpContextAccessor _http;
        public TenantConnection(IHttpContextAccessor http) { _http = http; }

        public string GetConnectionString()
        {
            var claim = _http.HttpContext?.User?.FindFirst(ConnClaim)?.Value;
            if (string.IsNullOrWhiteSpace(claim))
                throw new InvalidOperationException("Usuario no autenticado o sin conexión asignada.");
            return claim;
        }
    }
}
