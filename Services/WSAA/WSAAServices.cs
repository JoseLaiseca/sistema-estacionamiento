using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace EstacionamientoWebApp.Services.WSAA
{
    public class WSAAService
    {
        private readonly string _certPath;
        private readonly string _certPassword;
        private readonly string _wsaaUrl;
        private readonly ILogger<WSAAService> _logger;

        // Cache por servicio → YA no es tupla, ahora es WSAAResult
        private readonly Dictionary<string, WSAAResult> _tokenCache = new();

        public WSAAService(IConfiguration config, ILogger<WSAAService> logger)
        {
            _logger = logger;

            var afip = config.GetSection("AFIP");

            _certPath = afip["RutaCertificado"]!;
            _certPassword = afip["PasswordCertificado"]!;

            bool esProduccion = bool.Parse(afip["EsProduccion"] ?? "false");

            _wsaaUrl = esProduccion
                ? "https://wsaa.afip.gov.ar/ws/services/LoginCms"
                : "https://wsaahomo.afip.gov.ar/ws/services/LoginCms";
        }

        // ======================================================
        // MÉTODO PÚBLICO PROFESIONAL
        // ======================================================
        public async Task<WSAAResult> ObtenerCredencialesAsync(string servicio = "wsfe")
        {
            if (_tokenCache.TryGetValue(servicio, out var cached))
            {
                if (!cached.Expirado && cached.Expiration > DateTime.Now.AddMinutes(2))
                {
                    _logger.LogInformation("TA en caché para {Servicio}, expira: {Exp}", servicio, cached.Expiration);
                    return cached;
                }

                _logger.LogInformation("TA expirado para {Servicio}, generando nuevo...", servicio);
                _tokenCache.Remove(servicio);
            }

            _logger.LogInformation("Solicitando nuevo TA para {Servicio}...", servicio);

            var result = await SolicitarNuevoTA(servicio);

            _tokenCache[servicio] = result;
            return result;
        }

        // ======================================================
        // SOLICITAR NUEVO TA
        // ======================================================
        private async Task<WSAAResult> SolicitarNuevoTA(string servicio)
        {
            string tra = GenerarTRA(servicio);
            string cms = FirmarTRA(tra);
            string cmsBase64 = Convert.ToBase64String(Encoding.ASCII.GetBytes(cms));

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            string soapRequest = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <loginCms>
      <in0>{cmsBase64}</in0>
    </loginCms>
  </soap:Body>
</soap:Envelope>";

            var request = new HttpRequestMessage(HttpMethod.Post, _wsaaUrl)
            {
                Content = new StringContent(soapRequest, Encoding.UTF8, "text/xml")
            };

            request.Headers.Add("SOAPAction", "");

            var response = await client.SendAsync(request);
            string responseXml = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"WSAA Error HTTP: {response.StatusCode}, XML: {responseXml}");
            }

            var xmlDoc = XDocument.Parse(responseXml);
            string? loginCmsReturn = xmlDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "loginCmsReturn")?.Value;

            if (loginCmsReturn == null)
                throw new Exception("WSAA no devolvió loginCmsReturn.");

            return ProcesarLoginTicketResponse(loginCmsReturn, servicio);
        }

        // ======================================================
        // GENERAR TRA
        // ======================================================
        private string GenerarTRA(string servicio)
        {
            long uniqueId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string gen = DateTime.UtcNow.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ssZ");
            string exp = DateTime.UtcNow.AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ssZ");

            return $@"
<loginTicketRequest version=""1.0"">
    <header>
        <uniqueId>{uniqueId}</uniqueId>
        <generationTime>{gen}</generationTime>
        <expirationTime>{exp}</expirationTime>
    </header>
    <service>{servicio}</service>
</loginTicketRequest>".Trim();
        }

        // ======================================================
        // FIRMAR TRA
        // ======================================================
        private string FirmarTRA(string tra)
        {
            var cert = new X509Certificate2(_certPath, _certPassword,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.Exportable);

            var content = new ContentInfo(Encoding.UTF8.GetBytes(tra));
            var cms = new SignedCms(content);
            var signer = new CmsSigner(cert)
            {
                IncludeOption = X509IncludeOption.EndCertOnly
            };

            cms.ComputeSignature(signer);
            return Convert.ToBase64String(cms.Encode());
        }

        // ======================================================
        // PARSEAR LOGIN TICKET RESPONSE
        // ======================================================
        private WSAAResult ProcesarLoginTicketResponse(string xmlResponse, string servicio)
        {
            var xml = XDocument.Parse(xmlResponse);

            string token = xml.Root!.Element("credentials")!.Element("token")!.Value;
            string sign = xml.Root!.Element("credentials")!.Element("sign")!.Value;
            DateTime expiration = DateTime.Parse(xml.Root!.Element("header")!.Element("expirationTime")!.Value);

            return new WSAAResult
            {
                Servicio = servicio,
                Token = token,
                Sign = sign,
                Expiration = expiration
            };
        }
    }
}
