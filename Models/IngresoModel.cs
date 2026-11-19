using System;
using System.ComponentModel.DataAnnotations;

namespace EstacionamientoWebApp.Models
{
    public class IngresoModel
    {
        public int Id { get; set; }

        [Required]
        public DateTime Fecha { get; set; } = DateTime.Now;

        [Required]
        [Display(Name = "Tipo de vehículo")]
        public string TipoVehiculo { get; set; } = string.Empty;

        [MaxLength(8)]
        public string Patente { get; set; } = string.Empty;

        public string? Descripcion { get; set; }
    }
}
