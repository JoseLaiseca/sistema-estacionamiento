namespace EstacionamientoWebApp.Services.WSFE
{
    public class FEAuthRequest
    {
        public string Token { get; set; } = string.Empty;
        public string Sign { get; set; } = string.Empty;
        public long Cuit { get; set; }
    }
}

