using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EstacionamientoWebApp.Models
{
    public class Comprobante
    {
        public int Id { get; set; }

        [Required]
        [Range(1, 99999)]
        /// <summary>
        /// Punto de Venta AFIP. 
        /// En Producción debe ser 7 (RECE para aplicativo y web services).
        /// En Homologación puede ser 1 u otro valor configurado.
        /// </summary>
        public int PuntoVenta { get; set; } = 1;

        [Required]
        [Range(1, 999)]
        public int TipoComprobante { get; set; } // 01=Factura A, 06=Factura B, 11=Factura C

        [StringLength(20)]
        public string? NumeroComprobante { get; set; }

        [Required]
        public DateTime FechaEmision { get; set; } = DateTime.Now;

        public int TipoDoc { get; set; } = 99; // 99 = Consumidor Final

        [StringLength(20)]
        public string NumeroDoc { get; set; } = "0";

        [StringLength(11)]
        public string? CuitCliente { get; set; } // usado solo si TipoDoc = 80

        [StringLength(200)]
        public string? RazonSocialCliente { get; set; } = "Consumidor Final";

        [StringLength(200)]
        public string? DomicilioCliente { get; set; }

        [StringLength(20)]
        public string? CondicionVenta { get; set; } = "1"; // 1=Contado, 2=Cuenta Corriente

        [StringLength(2)]
        public string? Concepto { get; set; } = "1"; // 1=Productos, 2=Servicios, 3=Productos y Servicios

        public int? CondicionIVA { get; set; } = 4; // Condición frente al IVA del receptor según RG 5616: 1=RI, 2=RNI, 3=Exento, 4=CF, 5=Monotributo, 6=SNC, 7=NR, 8=Monotributo Social, 9=PCE, 10=PCE Social, 11=EDE

        public int? CondicionIVAReceptor { get; set; } = 5; // Condición IVA del receptor para Opcional 2101 (RG 5616): 1=RI, 2=RI-Agente, 3=Exento, 4=Monotributo, 5=Consumidor Final, 6=No alcanzado, 7=Monotributo Social

        [Column(TypeName = "decimal(12,2)")]
        public decimal ImporteTotal { get; set; }

        [Column(TypeName = "decimal(12,2)")]
        public decimal ImporteNeto { get; set; }

        [Column(TypeName = "decimal(12,2)")]
        public decimal ImporteIVA { get; set; }

        [StringLength(20)]
        public string? CAE { get; set; }

        public DateTime? FechaVtoCAE { get; set; }

        [StringLength(50)]
        public string Estado { get; set; } = "Pendiente"; // Pendiente, Autorizado, Rechazado

        [StringLength(500)]
        public string? Observaciones { get; set; }

        // Relación con detalles
        public List<DetalleComprobante> Detalles { get; set; } = new();

        // Campos de auditoría
        public DateTime FechaCreacion { get; set; } = DateTime.Now;
        public DateTime? FechaActualizacion { get; set; }
    }
}

