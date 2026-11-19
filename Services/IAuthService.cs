namespace EstacionamientoWebApp.Services
{
    public class AuthUser
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Playa { get; set; } = "";
        public string ConnectionString { get; set; } = "";
    }

    public interface IAuthService
    {
        Task<AuthUser?> ValidateUserAsync(string username, string password);
    }
}
