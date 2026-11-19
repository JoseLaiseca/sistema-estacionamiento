using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EstacionamientoWebApp.Models
{
    public class Mensual
    {
        public int Id { get; set; }

        // Modelo �actual� que us�s en Ingreso/Index
        [Required, StringLength(20)]
        public string Vehiculo { get; set; } = "";

        [Required, StringLength(8)]
        public string Patente { get; set; } = "";

        [Required, StringLength(120)]
        public string Nombre { get; set; } = "";

        [Range(0, 99999999)]
        public decimal Precio { get; set; }

        public bool DejoLlave { get; set; } = false;
        
        [StringLength(200)]
        public string? Descripcion { get; set; }

        public DateTime Fecha { get; set; } = DateTime.Now;
        public DateTime FechaVencimiento { get; set; } = DateTime.Now.AddMonths(1);

        public bool Activo { get; set; } = true;
        public bool Eliminado { get; set; } = false;

        // -----------------------------
        // Aliases de compatibilidad (NO mapean a DB)
        // -----------------------------

        // Algunas vistas viejas usan FechaInicio/FechaFin => mapeamos a Fecha/FechaVencimiento
        [NotMapped]
        public DateTime? FechaInicio
        {
            get => Fecha;
            set { if (value.HasValue) Fecha = value.Value; }
        }

        [NotMapped]
        public DateTime? FechaFin
        {
            get => FechaVencimiento;
            set { if (value.HasValue) FechaVencimiento = value.Value; }
        }

        // Algunas usan PrecioMensual => mapeamos a Precio
        [NotMapped]
        public decimal? PrecioMensual
        {
            get => Precio;
            set { if (value.HasValue) Precio = value.Value; }
        }

        // Si ten�as campos informativos que no existen en DB, los dejamos como NotMapped
        [NotMapped]
        public string? Telefono { get; set; }

        [NotMapped]
        public string? Observaciones { get; set; }
    }
}

