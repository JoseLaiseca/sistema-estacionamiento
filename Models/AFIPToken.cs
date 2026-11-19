namespace EstacionamientoWebApp.Models
{
    public class AFIPToken
    {
        public string Token { get; set; } = string.Empty;
        public string Sign { get; set; } = string.Empty;
        public DateTime ExpirationTime { get; set; }
        public string Cuit { get; set; } = string.Empty;
        public bool IsValid => !string.IsNullOrEmpty(Token) && !string.IsNullOrEmpty(Sign) && ExpirationTime > DateTime.Now;
    }
}

