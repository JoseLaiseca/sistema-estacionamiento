using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EstacionamientoWebApp.Models
{
    public class DetalleComprobante
    {
        public int Id { get; set; }

        [Required]
        public int ComprobanteId { get; set; }

        [ForeignKey("ComprobanteId")]
        public Comprobante? Comprobante { get; set; }

        [StringLength(200)]
        public string? Descripcion { get; set; }

        [Column(TypeName = "decimal(10,2)")]
        public decimal Cantidad { get; set; } = 1;

        [Column(TypeName = "decimal(12,2)")]
        public decimal PrecioUnitario { get; set; }

        [Column(TypeName = "decimal(5,2)")]
        public decimal AlicuotaIVA { get; set; } = 21; // 0, 10.5, 21, 27

        [NotMapped]
        public decimal Subtotal => Cantidad * PrecioUnitario;

        // ⚠️ CRÍTICO: El precio unitario es NETO (NO incluye IVA)
        // Fórmula correcta:
        //   ImporteNeto = Cantidad * PrecioUnitario (precio ya es neto)
        //   ImporteIVA = ImporteNeto * (AlicuotaIVA / 100)
        [NotMapped]
        public decimal ImporteNeto => Cantidad * PrecioUnitario;

        [NotMapped]
        public decimal ImporteIVA => ImporteNeto * (AlicuotaIVA / 100);

        // Campos adicionales para AFIP
        [StringLength(50)]
        public string? CodigoProducto { get; set; }

        [StringLength(10)]
        public string? UnidadMedida { get; set; } = "7"; // 7=Unidad
    }
}

