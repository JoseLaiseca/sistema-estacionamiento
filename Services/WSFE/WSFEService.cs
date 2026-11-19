using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;

namespace EstacionamientoWebApp.Services.WSFE
{
    /// <summary>
    /// Cliente para el servicio WSFE de AFIP
    /// </summary>
    public class WSFEService
    {
        private readonly string _urlWSFE;
        private readonly string _certificadoPath;
        private readonly string _certPassword;
        private readonly ILogger<WSFEService>? _logger;

        public WSFEService(string urlWSFE, string certificadoPath, string certPassword, ILogger<WSFEService>? logger = null)
        {
            _urlWSFE = urlWSFE;
            _certificadoPath = certificadoPath;
            _certPassword = certPassword;
            _logger = logger;
        }

        /// <summary>
        /// Solicita autorización de comprobante electrónico (FECAESolicitar)
        /// </summary>
        public async Task<FECAEResponse?> FECAESolicitarAsync(FEAuthRequest auth, FECAERequest request)
        {
            try
            {
                _logger?.LogInformation("Enviando FECAESolicitar a WSFE");

                // Construir XML SOAP
                string soapRequest = ConstruirSOAPFECAESolicitar(auth, request);

                // Crear HttpClient con certificado
                var handler = new HttpClientHandler();
                if (!string.IsNullOrEmpty(_certificadoPath) && File.Exists(_certificadoPath))
                {
                    var cert = new X509Certificate2(_certificadoPath, _certPassword);
                    handler.ClientCertificates.Add(cert);
                }

                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, _urlWSFE)
                {
                    Content = new StringContent(soapRequest, Encoding.UTF8, "text/xml")
                };

                httpRequest.Headers.Add("SOAPAction", "\"http://ar.gov.afip.dif.FEV1/FECAESolicitar\"");

                var response = await client.SendAsync(httpRequest);
                string responseXml = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogError("Error HTTP {StatusCode}: {Response}", response.StatusCode, responseXml);
                    return null;
                }

                // Procesar respuesta
                return ProcesarRespuestaFECAESolicitar(responseXml);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error en FECAESolicitar");
                return null;
            }
        }

        private string ConstruirSOAPFECAESolicitar(FEAuthRequest auth, FECAERequest request)
        {
            var sb = new StringBuilder();
            sb.AppendLine(@"<?xml version=""1.0"" encoding=""utf-8""?>");
            sb.AppendLine(@"<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance""");
            sb.AppendLine(@"               xmlns:xsd=""http://www.w3.org/2001/XMLSchema""");
            sb.AppendLine(@"               xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">");
            sb.AppendLine(@"  <soap:Body>");
            sb.AppendLine(@"    <FECAESolicitar xmlns=""http://ar.gov.afip.dif.FEV1/"">");
            sb.AppendLine(@"      <Auth>");
            sb.AppendLine($@"        <Token>{auth.Token}</Token>");
            sb.AppendLine($@"        <Sign>{auth.Sign}</Sign>");
            sb.AppendLine($@"        <Cuit>{auth.Cuit}</Cuit>");
            sb.AppendLine(@"      </Auth>");
            sb.AppendLine(@"      <FeCAEReq>");
            sb.AppendLine(@"        <FeCabReq>");
            sb.AppendLine($@"          <CantReg>{request.FeCabReq.CantReg}</CantReg>");
            sb.AppendLine($@"          <PtoVta>{request.FeCabReq.PtoVta}</PtoVta>");
            sb.AppendLine($@"          <CbteTipo>{request.FeCabReq.CbteTipo}</CbteTipo>");
            sb.AppendLine(@"        </FeCabReq>");
            sb.AppendLine(@"        <FeDetReq>");

            foreach (var det in request.FeDetReq)
            {
                sb.AppendLine(@"          <FECAEDetRequest>");
                sb.AppendLine($@"            <Concepto>{det.Concepto}</Concepto>");
                sb.AppendLine($@"            <DocTipo>{det.DocTipo}</DocTipo>");
                sb.AppendLine($@"            <DocNro>{det.DocNro}</DocNro>");
                sb.AppendLine($@"            <CbteDesde>{det.CbteDesde}</CbteDesde>");
                sb.AppendLine($@"            <CbteHasta>{det.CbteHasta}</CbteHasta>");
                sb.AppendLine($@"            <CbteFch>{det.CbteFch}</CbteFch>");
                // ⚠️ CRÍTICO: Usar CultureInfo.InvariantCulture para asegurar punto (.) como separador decimal
                // AFIP no acepta coma (,) como separador decimal
                sb.AppendLine($@"            <ImpTotal>{det.ImpTotal.ToString("F2", CultureInfo.InvariantCulture)}</ImpTotal>");
                sb.AppendLine($@"            <ImpTotConc>{det.ImpTotConc.ToString("F2", CultureInfo.InvariantCulture)}</ImpTotConc>");
                sb.AppendLine($@"            <ImpNeto>{det.ImpNeto.ToString("F2", CultureInfo.InvariantCulture)}</ImpNeto>");
                sb.AppendLine($@"            <ImpIVA>{det.ImpIVA.ToString("F2", CultureInfo.InvariantCulture)}</ImpIVA>");
                sb.AppendLine($@"            <ImpTrib>{det.ImpTrib.ToString("F2", CultureInfo.InvariantCulture)}</ImpTrib>");
                sb.AppendLine($@"            <MonId>{det.MonId}</MonId>");
                sb.AppendLine($@"            <MonCotiz>{det.MonCotiz.ToString("F2", CultureInfo.InvariantCulture)}</MonCotiz>");
                
                // ⚠️ CRÍTICO: CondicionIvaReceptor es obligatorio según RG 5616
                int condicionIVA = det.CondicionIvaReceptor ?? 4; // Por defecto: 4 = Consumidor Final
                if (condicionIVA < 1 || condicionIVA > 11)
                {
                    condicionIVA = 4; // Validar rango válido (1-11)
                }
                sb.AppendLine($@"            <CondicionIvaReceptor>{condicionIVA}</CondicionIvaReceptor>");

                if (det.Iva != null && det.Iva.Length > 0)
                {
                    sb.AppendLine(@"            <Iva>");
                    foreach (var iva in det.Iva)
                    {
                        sb.AppendLine(@"              <AlicIva>");
                        sb.AppendLine($@"                <Id>{iva.Id}</Id>");
                        sb.AppendLine($@"                <BaseImp>{iva.BaseImp.ToString("F2", CultureInfo.InvariantCulture)}</BaseImp>");
                        sb.AppendLine($@"                <Importe>{iva.Importe.ToString("F2", CultureInfo.InvariantCulture)}</Importe>");
                        sb.AppendLine(@"              </AlicIva>");
                    }
                    sb.AppendLine(@"            </Iva>");
                }

                sb.AppendLine(@"          </FECAEDetRequest>");
            }

            sb.AppendLine(@"        </FeDetReq>");
            sb.AppendLine(@"      </FeCAEReq>");
            sb.AppendLine(@"    </FECAESolicitar>");
            sb.AppendLine(@"  </soap:Body>");
            sb.AppendLine(@"</soap:Envelope>");

            return sb.ToString();
        }

        private FECAEResponse? ProcesarRespuestaFECAESolicitar(string responseXml)
        {
            try
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(responseXml);

                var nsManager = new XmlNamespaceManager(xmlDoc.NameTable);
                nsManager.AddNamespace("soap", "http://schemas.xmlsoap.org/soap/envelope/");
                nsManager.AddNamespace("fe", "http://ar.gov.afip.dif.FEV1/");

                var response = new FECAEResponse();

                // Procesar FeCabResp
                var feCabRespNode = xmlDoc.SelectSingleNode("//fe:FeCabResp", nsManager);
                if (feCabRespNode != null)
                {
                    response.FeCabResp = new FECAECabResponse
                    {
                        CantReg = int.Parse(feCabRespNode.SelectSingleNode("fe:CantReg", nsManager)?.InnerText ?? "0"),
                        Resultado = feCabRespNode.SelectSingleNode("fe:Resultado", nsManager)?.InnerText
                    };
                }

                // Procesar FeDetResp
                var feDetRespNodes = xmlDoc.SelectNodes("//fe:FECAEDetResponse", nsManager);
                if (feDetRespNodes != null && feDetRespNodes.Count > 0)
                {
                    var detResponses = new List<FECAEDetResponse>();
                    foreach (XmlNode node in feDetRespNodes)
                    {
                        var detResp = new FECAEDetResponse
                        {
                            Concepto = int.Parse(node.SelectSingleNode("fe:Concepto", nsManager)?.InnerText ?? "0"),
                            DocTipo = int.Parse(node.SelectSingleNode("fe:DocTipo", nsManager)?.InnerText ?? "0"),
                            DocNro = long.Parse(node.SelectSingleNode("fe:DocNro", nsManager)?.InnerText ?? "0"),
                            CbteDesde = int.Parse(node.SelectSingleNode("fe:CbteDesde", nsManager)?.InnerText ?? "0"),
                            CbteHasta = int.Parse(node.SelectSingleNode("fe:CbteHasta", nsManager)?.InnerText ?? "0"),
                            CbteFch = node.SelectSingleNode("fe:CbteFch", nsManager)?.InnerText ?? "",
                            Resultado = node.SelectSingleNode("fe:Resultado", nsManager)?.InnerText,
                            CAE = node.SelectSingleNode("fe:CAE", nsManager)?.InnerText,
                            CAEFchVto = node.SelectSingleNode("fe:CAEFchVto", nsManager)?.InnerText
                        };

                        // Procesar observaciones
                        var obsNodes = node.SelectNodes("fe:Observaciones/fe:Obs", nsManager);
                        if (obsNodes != null && obsNodes.Count > 0)
                        {
                            var obsList = new List<string>();
                            foreach (XmlNode obsNode in obsNodes)
                            {
                                obsList.Add(obsNode.InnerText);
                            }
                            detResp.Observaciones = string.Join("; ", obsList);
                        }

                        detResponses.Add(detResp);
                    }
                    response.FeDetResp = detResponses.ToArray();
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error procesando respuesta FECAESolicitar");
                return null;
            }
        }
    }
}

