using System;
using System.Threading.Tasks;

namespace EstacionamientoWebApp.Services
{
    public class TarifasService : ITarifasService
    {
        // Tramos fijos de ejemplo:
        //  0–65'  $1500
        // 66–75'  $2000
        // 76–120' $800
        // >120'   $800 + bloques de 60' a $1500
        private static readonly TramoTarifa Tramo1 = new(1, 0, 65, 1500m);
        private static readonly TramoTarifa Tramo2 = new(2, 66, 75, 2000m);
        private static readonly TramoTarifa Tramo3 = new(3, 76, 120, 800m);

        private const decimal PrecioHoraExtra = 1500m;

        public Task<(decimal importe, int minutos, TramoTarifa? tramo)> CalcularImporteAsync(DateTime entrada, DateTime salida)
        {
            if (salida < entrada)
            {
                // si cruzó medianoche
                salida = salida.AddDays(1);
            }

            var minutos = (int)Math.Ceiling((salida - entrada).TotalMinutes);
            if (minutos < 0) minutos = 0;

            if (minutos <= Tramo1.MinutosHasta)
                return Task.FromResult<(decimal importe, int minutos, TramoTarifa? tramo)>((Tramo1.ImporteFijo, minutos, Tramo1));

            if (minutos <= Tramo2.MinutosHasta)
                return Task.FromResult<(decimal importe, int minutos, TramoTarifa? tramo)>((Tramo2.ImporteFijo, minutos, Tramo2));

            if (minutos <= Tramo3.MinutosHasta)
                return Task.FromResult<(decimal importe, int minutos, TramoTarifa? tramo)>((Tramo3.ImporteFijo, minutos, Tramo3));

            // Superó 120': cobrar Tramo3 + horas extra
            var extraMin = minutos - Tramo3.MinutosHasta;
            var horasExtra = (int)Math.Ceiling(extraMin / 60.0);
            var importe = Tramo3.ImporteFijo + horasExtra * PrecioHoraExtra;

            return Task.FromResult((importe, minutos, (TramoTarifa?)null));
        }
    }
}
