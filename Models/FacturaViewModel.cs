using System.ComponentModel.DataAnnotations;

namespace EstacionamientoWebApp.Models
{
    /// <summary>
    /// ViewModel para crear facturas desde ingresos o estadías
    /// </summary>
    public class FacturaViewModel
    {
        public int? IngresoId { get; set; }
        public int? EstadiaId { get; set; }
        
        [Display(Name = "Cliente")]
        public string Cliente { get; set; } = string.Empty;
        
        [Display(Name = "Patente")]
        public string Patente { get; set; } = string.Empty;
        
        [Display(Name = "Descripción")]
        public string Descripcion { get; set; } = string.Empty;
        
        [Display(Name = "Importe")]
        [Required(ErrorMessage = "El importe es requerido")]
        [Range(0.01, double.MaxValue, ErrorMessage = "El importe debe ser mayor a 0")]
        public decimal Importe { get; set; }
    }
}

