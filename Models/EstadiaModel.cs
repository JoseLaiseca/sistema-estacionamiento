
using System;
using System.ComponentModel.DataAnnotations;

namespace EstacionamientoWebApp.Models
{
    public class EstadiaModel
    {
        public int Id { get; set; }

        [Required]
        public string NombreCliente { get; set; } = string.Empty;

        [Required]
        public string Celular { get; set; } = string.Empty;

        [Required]
        public string Patente { get; set; } = string.Empty;

        [Required]
        public string TipoVehiculo { get; set; } = string.Empty;

        public string? Descripcion { get; set; }

        [Required]
        public bool DejoLlave { get; set; }

        [Required]
        public int DiasEstadia { get; set; }

        [Required]
        [DataType(DataType.Currency)]
        public decimal Monto { get; set; }

        public DateTime FechaIngreso { get; set; } = DateTime.Now;
    }
}
