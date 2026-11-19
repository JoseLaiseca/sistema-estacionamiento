using BCrypt.Net;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Dapper;

namespace EstacionamientoWebApp.Scripts
{
    /// <summary>
    /// Script para generar hashes de contraseñas y migrar usuarios existentes
    /// </summary>
    public static class PasswordMigrationTool
    {
        public static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                ShowUsage();
                return;
            }

            string command = args[0].ToLower();

            switch (command)
            {
                case "hash":
                    if (args.Length < 2)
                    {
                        Console.WriteLine("Uso: GeneratePasswordHash hash <contraseña>");
                        return;
                    }
                    GenerateHash(args[1]);
                    break;

                case "migrate":
                    MigrateAllPasswords();
                    break;

                case "check":
                    CheckUserPasswords();
                    break;

                default:
                    ShowUsage();
                    break;
            }
        }

        private static void ShowUsage()
        {
            Console.WriteLine("=== Script de Gestión de Contraseñas ===");
            Console.WriteLine();
            Console.WriteLine("Comandos disponibles:");
            Console.WriteLine("  hash <contraseña>     - Genera hash para una contraseña");
            Console.WriteLine("  migrate               - Migra todas las contraseñas a BCrypt");
            Console.WriteLine("  check                 - Verifica el estado de las contraseñas");
            Console.WriteLine();
            Console.WriteLine("Ejemplos:");
            Console.WriteLine("  GeneratePasswordHash hash admin123");
            Console.WriteLine("  GeneratePasswordHash migrate");
            Console.WriteLine("  GeneratePasswordHash check");
        }

        private static void GenerateHash(string password)
        {
            string hash = BCrypt.Net.BCrypt.HashPassword(password);
            
            Console.WriteLine($"Contraseña: {password}");
            Console.WriteLine($"Hash BCrypt: {hash}");
            Console.WriteLine();
            Console.WriteLine("SQL para actualizar la base de datos:");
            Console.WriteLine($"UPDATE Users SET PasswordHash = '{hash}' WHERE Username = 'tu_usuario';");
        }

        private static void MigrateAllPasswords()
        {
            try
            {
                // Leer configuración
                var config = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json")
                    .Build();

                string? connectionString = config.GetConnectionString("AuthConnection");
                if (string.IsNullOrEmpty(connectionString))
                {
                    Console.WriteLine("Error: No se encontró AuthConnection en appsettings.json");
                    return;
                }

                using var connection = new SqlConnection(connectionString);
                connection.Open();

                // Obtener usuarios con contraseñas no BCrypt
                var users = connection.Query<dynamic>(
                    "SELECT Id, Username, PasswordHash FROM dbo.Users WHERE PasswordHash NOT LIKE '$2%'");

                Console.WriteLine($"Encontrados {users.Count()} usuarios para migrar:");
                Console.WriteLine();

                foreach (var user in users)
                {
                    Console.WriteLine($"Usuario: {user.Username}");
                    Console.WriteLine($"Hash actual: {user.PasswordHash}");
                    
                    // Solicitar nueva contraseña
                    Console.Write("Ingrese nueva contraseña para este usuario: ");
                    string newPassword = Console.ReadLine() ?? "";
                    
                    if (!string.IsNullOrEmpty(newPassword))
                    {
                        string newHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
                        
                        // Actualizar en la base de datos
                        int rowsAffected = connection.Execute(
                            "UPDATE dbo.Users SET PasswordHash = @hash WHERE Id = @id",
                            new { hash = newHash, id = user.Id });
                        
                        if (rowsAffected > 0)
                        {
                            Console.WriteLine($"✅ Usuario {user.Username} migrado exitosamente");
                        }
                        else
                        {
                            Console.WriteLine($"❌ Error migrando usuario {user.Username}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("⚠️ Saltando usuario (contraseña vacía)");
                    }
                    
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error durante la migración: {ex.Message}");
            }
        }

        private static void CheckUserPasswords()
        {
            try
            {
                // Leer configuración
                var config = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json")
                    .Build();

                string? connectionString = config.GetConnectionString("AuthConnection");
                if (string.IsNullOrEmpty(connectionString))
                {
                    Console.WriteLine("Error: No se encontró AuthConnection en appsettings.json");
                    return;
                }

                using var connection = new SqlConnection(connectionString);
                connection.Open();

                var users = connection.Query<dynamic>("SELECT Username, PasswordHash FROM dbo.Users");

                Console.WriteLine("=== Estado de Contraseñas ===");
                Console.WriteLine();

                foreach (var user in users)
                {
                    bool isBcrypt = user.PasswordHash.StartsWith("$2");
                    string status = isBcrypt ? "✅ BCrypt" : "⚠️ Texto plano";
                    
                    Console.WriteLine($"Usuario: {user.Username}");
                    Console.WriteLine($"Estado: {status}");
                    Console.WriteLine($"Hash: {user.PasswordHash}");
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error verificando contraseñas: {ex.Message}");
            }
        }
    }
}
