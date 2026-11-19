using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace EstacionamientoWebApp.Services
{
    public class AuthService : IAuthService
    {
        private readonly string _authConn;

        public AuthService(IConfiguration cfg)
        {
            _authConn = cfg.GetConnectionString("AuthConnection")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:AuthConnection");
        }

        public async Task<AuthUser?> ValidateUserAsync(string username, string password)
        {
            using var con = new SqlConnection(_authConn);
            var user = await con.QuerySingleOrDefaultAsync<AuthUser>(
                "SELECT TOP 1 * FROM dbo.Users WHERE Username = @u", new { u = username });

            if (user is null || !IsValidBcryptHash(user.PasswordHash))
                return null;

            return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash) ? user : null;
        }

        /// <summary>
        /// Genera un hash seguro de contraseña usando BCrypt.
        /// </summary>
        public static string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password);
        }

        // Los hashes BCrypt válidos empiezan con $2a$, $2b$, $2x$ o $2y$.
        private static bool IsValidBcryptHash(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return false;

            return hash.StartsWith("$2a$") || hash.StartsWith("$2b$") ||
                   hash.StartsWith("$2x$") || hash.StartsWith("$2y$");
        }
    }
}
