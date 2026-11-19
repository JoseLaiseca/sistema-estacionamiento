using System;
using System.ComponentModel.DataAnnotations;

namespace EstacionamientoWebApp.Models
{
    public class CobroModel
    {
        public string TipoVehiculo { get; set; } = string.Empty;
        public string Patente { get; set; } = string.Empty;
        public string HoraEntrada { get; set; } = string.Empty;
        public string HoraActual { get; set; } = string.Empty;
        public decimal Monto { get; set; }
    }
}
