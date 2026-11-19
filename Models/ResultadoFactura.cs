namespace EstacionamientoWebApp.Models
{
    /// <summary>
    /// Resultado de la emisión de una factura electrónica por AFIP
    /// </summary>
    public class ResultadoFactura
    {
        /// <summary>
        /// Código de Autorización Electrónico (CAE) asignado por AFIP
        /// </summary>
        public string CAE { get; set; } = string.Empty;

        /// <summary>
        /// Fecha de vencimiento del CAE
        /// </summary>
        public DateTime FechaVencimiento { get; set; }

        /// <summary>
        /// Número de comprobante asignado por AFIP
        /// </summary>
        public long NumeroComprobante { get; set; }

        /// <summary>
        /// Indica si la factura fue autorizada exitosamente
        /// </summary>
        public bool Autorizado { get; set; }

        /// <summary>
        /// Observaciones o mensajes de error de AFIP
        /// </summary>
        public string? Observaciones { get; set; }
    }
}

