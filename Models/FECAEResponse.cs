namespace EstacionamientoWebApp.Models
{
    /// <summary>
    /// Clase de respuesta de AFIP para FECAESolicitar
    /// Compatible con el formato esperado por el controlador
    /// </summary>
    public class FECAEResponse
    {
        public FECAEDetResponse[]? FeDetResp { get; set; }
    }

    public class FECAEDetResponse
    {
        public string CAE { get; set; } = string.Empty;
        public string CAEFchVto { get; set; } = string.Empty;
        public int CbteDesde { get; set; }
        public int CbteHasta { get; set; }
        public string Resultado { get; set; } = string.Empty;
        public string? Observaciones { get; set; }
    }
}













