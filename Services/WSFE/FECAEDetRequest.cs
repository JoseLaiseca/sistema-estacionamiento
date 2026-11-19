namespace EstacionamientoWebApp.Services.WSFE
{
    public class FECAEDetRequest
    {
        public int Concepto { get; set; }
        public int DocTipo { get; set; }
        public long DocNro { get; set; }
        public int CbteDesde { get; set; }
        public int CbteHasta { get; set; }
        public string CbteFch { get; set; } = string.Empty;
        public double ImpTotal { get; set; }
        public double ImpTotConc { get; set; }
        public double ImpNeto { get; set; }
        public double ImpIVA { get; set; }
        public double ImpTrib { get; set; }
        public string MonId { get; set; } = "PES";
        public double MonCotiz { get; set; } = 1.0;
        public int? CondicionIvaReceptor { get; set; } = 4; // Condición frente al IVA del receptor (obligatorio según RG 5616)
        public AlicIva[]? Iva { get; set; }
    }
}

