namespace EstacionamientoWebApp.Models
{
    public class PersonaAFIP
    {
        public string Cuit { get; set; } = string.Empty;
        public string RazonSocial { get; set; } = string.Empty;
        public string TipoPersona { get; set; } = string.Empty;
        public string Estado { get; set; } = string.Empty;
        public string Impuestos { get; set; } = string.Empty;
        public string Actividades { get; set; } = string.Empty;
        public string DomicilioFiscal { get; set; } = string.Empty;
        public DateTime FechaInscripcion { get; set; }
        public int CondicionIVA { get; set; } = 4; // Por defecto Consumidor Final (4)
        public bool EsValido => !string.IsNullOrEmpty(Cuit) && !string.IsNullOrEmpty(RazonSocial);
    }
}

