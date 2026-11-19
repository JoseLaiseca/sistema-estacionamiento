namespace EstacionamientoWebApp.Services.WSFE
{
    public class FECAEResponse
    {
        public FECAECabResponse? FeCabResp { get; set; }
        public FECAEDetResponse[]? FeDetResp { get; set; }
        public string? Observaciones { get; set; }
    }

    public class FECAECabResponse
    {
        public int CantReg { get; set; }
        public string? Resultado { get; set; }
        public string? Reproceso { get; set; }
    }
}

