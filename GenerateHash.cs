using System;
using BCrypt.Net;

class HashGenerator
{
    static void Main()
    {
        string hash = BCrypt.Net.BCrypt.HashPassword("admin");
        Console.WriteLine(hash);
    }
}


//using System;
//using BCrypt.Net;

//namespace EstacionamientoWebApp
//{
//    public static class GenerateHash
//    {
//        public static void Main()
//        {
//            // Generar hash BCrypt para la contraseña "1234"
//            string password = "1234";
//            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(password);

//            Console.WriteLine("=== GENERADOR DE HASH BCrypt ===");
//            Console.WriteLine($"Contraseña original: {password}");
//            Console.WriteLine($"Hash BCrypt generado: {hashedPassword}");
//            Console.WriteLine();
//        }
//    }
//}



