using System;

namespace TestTarifas
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("=== PRUEBA DE CÁLCULO DE TARIFAS PARA MOTOS CON TOLERANCIA ===");
            Console.WriteLine();
            
            // Probar diferentes tiempos, especialmente alrededor de los límites de tolerancia
            int[] tiempos = { 30, 60, 75, 90, 105, 120, 135, 150, 165, 180 };
            
            foreach (int minutos in tiempos)
            {
                decimal monto = CalcularMontoFlexible(3, minutos);
                int horas = minutos / 60;
                int minutosRestantes = minutos % 60;
                
                Console.WriteLine($"Tiempo: {minutos} minutos ({horas}h {minutosRestantes}m)");
                Console.WriteLine($"Monto: ${monto}");
                
                if (minutosRestantes <= 15 && minutosRestantes > 0)
                {
                    Console.WriteLine($"Descripción: {horas} horas completas + {minutosRestantes} minutos (TOLERANCIA - no se cobra extra)");
                }
                else if (minutosRestantes > 15)
                {
                    Console.WriteLine($"Descripción: {horas} horas completas + {minutosRestantes} minutos (SUPERA TOLERANCIA - se cobra hora adicional)");
                }
                else
                {
                    Console.WriteLine($"Descripción: {horas} horas exactas");
                }
                Console.WriteLine("---");
            }
        }
        
        static decimal CalcularMontoFlexible(int tipoId, int minutos)
        {
            // Lógica especial para motos
            if (tipoId == 3)
            {
                // Para motos: $1000 por hora, pero con lógica sucesiva y tolerancia
                int horasMoto = minutos / 60;
                int minutosMoto = minutos % 60;
                
                // Si no hay horas completas, cobrar mínimo 1 hora
                if (horasMoto == 0)
                    return 1000m;
                
                // Calcular monto base por horas completas
                decimal montoMoto = horasMoto * 1000m;
                
                // Aplicar tolerancia de 15 minutos antes de cobrar la hora siguiente
                if (minutosMoto <= 15)
                {
                    // Tolerancia de 15 minutos: no se cobra extra
                    return montoMoto;
                }
                else
                {
                    // Supera la tolerancia: se cobra la hora siguiente
                    return montoMoto + 1000m;
                }
            }
            
            return 0m;
        }
    }
}