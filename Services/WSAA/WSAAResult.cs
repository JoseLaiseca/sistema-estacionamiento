using Microsoft.AspNetCore.Mvc;

namespace EstacionamientoWebApp.Services.WSAA
{
    public class WSAAResult
    {
        public string Servicio { get; set; } = "";
        public string Token { get; set; } = "";
        public string Sign { get; set; } = "";
        public DateTime Expiration { get; set; }
        public bool Expirado => Expiration <= DateTime.Now;
    }
}
