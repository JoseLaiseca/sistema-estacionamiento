namespace EstacionamientoWebApp.Services.WSFE
{
    public class FECAEDetResponse
    {
        public int Concepto { get; set; }
        public int DocTipo { get; set; }
        public long DocNro { get; set; }
        public int CbteDesde { get; set; }
        public int CbteHasta { get; set; }
        public string CbteFch { get; set; } = string.Empty;
        public string? Resultado { get; set; }
        public string? CAE { get; set; }
        public string? CAEFchVto { get; set; }
        public string? Observaciones { get; set; }
    }
}

