using System;
using System.Threading.Tasks;

namespace EstacionamientoWebApp.Services
{
    public record TramoTarifa(int Id, int MinutosDesde, int MinutosHasta, decimal ImporteFijo);

    public interface ITarifasService
    {
        /// <summary>
        /// Calcula importe entre dos tiempos. Devuelve (importe, minutosTotales, tramoAplicado).
        /// Si no aplica tramo fijo (por superar el rango), tramo será null.
        /// </summary>
        Task<(decimal importe, int minutos, TramoTarifa? tramo)> CalcularImporteAsync(DateTime entrada, DateTime salida);
    }
}
