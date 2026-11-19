namespace EstacionamientoWebApp.Services.WSFE
{
    public class FECAERequest
    {
        public FECAECabRequest FeCabReq { get; set; } = new();
        public FECAEDetRequest[] FeDetReq { get; set; } = Array.Empty<FECAEDetRequest>();
    }
}

