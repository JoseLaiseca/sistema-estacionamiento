using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Xml;

namespace EstacionamientoWebApp.Ejemplos
{
    /// <summary>
    /// Ejemplo de referencia para implementación de WS_SR_PADRON_A5 de AFIP
    /// Basado en documentación oficial y mejores prácticas
    /// </summary>
    public class EjemploAFIPPadronA5
    {
        private readonly string _urlWSP5;
        private readonly string _cuitServicio;
        private readonly HttpClient _httpClient;

        public EjemploAFIPPadronA5(string urlWSP5, string cuitServicio)
        {
            _urlWSP5 = urlWSP5;
            _cuitServicio = cuitServicio;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        /// <summary>
        /// Ejemplo de cómo crear el XML correctamente para getPersona_v2
        /// IMPORTANTE: El orden de los elementos es CRÍTICO
        /// </summary>
        public string CrearXMLConsulta(string cuitConsultado, string token, string sign)
        {
            // ⚠️ CRÍTICO: El orden debe ser EXACTAMENTE:
            // 1. token
            // 2. sign
            // 3. cuitRepresentada (CUIT del certificado)
            // 4. idPersona (CUIT a consultar)
            
            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/""
                  xmlns:a5=""http://a5.soap.ws.server.puc.sr/"">
   <soapenv:Header/>
   <soapenv:Body>
      <a5:getPersona_v2>
         <token>{token}</token>
         <sign>{sign}</sign>
         <cuitRepresentada>{_cuitServicio}</cuitRepresentada>
         <idPersona>{cuitConsultado}</idPersona>
      </a5:getPersona_v2>
   </soapenv:Body>
</soapenv:Envelope>";
        }

        /// <summary>
        /// Ejemplo de cómo procesar una respuesta exitosa de AFIP
        /// </summary>
        public class PersonaResultado
        {
            public string Cuit { get; set; }
            public string RazonSocial { get; set; }
            public string Estado { get; set; }
            public string TipoPersona { get; set; }
            public string CondicionIVA { get; set; }
            public string Domicilio { get; set; }
        }

        /// <summary>
        /// Ejemplo de respuesta XML exitosa de AFIP (formato esperado)
        /// </summary>
        public PersonaResultado ProcesarRespuestaExitosa(string xmlRespuesta)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xmlRespuesta);

                // Namespace manager para manejar los namespaces de AFIP
                var nsManager = new XmlNamespaceManager(doc.NameTable);
                nsManager.AddNamespace("soap", "http://schemas.xmlsoap.org/soap/envelope/");
                nsManager.AddNamespace("a5", "http://a5.soap.ws.server.puc.sr/");

                // Buscar el nodo personaReturn (respuesta exitosa)
                var personaNode = doc.SelectSingleNode("//a5:personaReturn", nsManager);
                
                if (personaNode == null)
                {
                    // Intentar sin namespace
                    personaNode = doc.SelectSingleNode("//personaReturn");
                }

                if (personaNode == null)
                {
                    throw new Exception("No se encontró personaReturn en la respuesta");
                }

                var resultado = new PersonaResultado();

                // Extraer datos usando XPath
                var cuitNode = personaNode.SelectSingleNode(".//idPersona") ?? 
                               personaNode.SelectSingleNode(".//cuit");
                resultado.Cuit = cuitNode?.InnerText ?? "";

                var razonNode = personaNode.SelectSingleNode(".//razonSocial") ?? 
                                personaNode.SelectSingleNode(".//denominacion") ??
                                personaNode.SelectSingleNode(".//nombre");
                resultado.RazonSocial = razonNode?.InnerText ?? "";

                var estadoNode = personaNode.SelectSingleNode(".//estadoClave");
                resultado.Estado = estadoNode?.InnerText ?? "ACTIVO";

                var tipoNode = personaNode.SelectSingleNode(".//tipoPersona");
                resultado.TipoPersona = tipoNode?.InnerText ?? "";

                var condicionNode = personaNode.SelectSingleNode(".//condicionIVA") ??
                                   personaNode.SelectSingleNode(".//condicionFrenteIVA");
                resultado.CondicionIVA = condicionNode?.InnerText ?? "4";

                var domicilioNode = personaNode.SelectSingleNode(".//domicilioFiscal") ??
                                   personaNode.SelectSingleNode(".//domicilio");
                resultado.Domicilio = domicilioNode?.InnerText ?? "";

                return resultado;
            }
            catch (Exception ex)
            {
                throw new Exception($"Error al procesar respuesta: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Ejemplo de cómo manejar errores de AFIP
        /// </summary>
        public string ProcesarError(string xmlRespuesta)
        {
            try
            {
                var doc = new XmlDocument();
                doc.LoadXml(xmlRespuesta);

                // Buscar faultstring (error SOAP)
                var faultString = doc.SelectSingleNode("//faultstring")?.InnerText;
                if (!string.IsNullOrEmpty(faultString))
                {
                    return faultString;
                }

                // Buscar mensaje de error alternativo
                var errorNode = doc.SelectSingleNode("//error") ?? 
                               doc.SelectSingleNode("//message");
                if (errorNode != null)
                {
                    return errorNode.InnerText;
                }

                return "Error desconocido de AFIP";
            }
            catch
            {
                return "Error al procesar respuesta de error";
            }
        }

        /// <summary>
        /// Ejemplo completo de consulta
        /// </summary>
        public async Task<PersonaResultado> ConsultarPersonaAsync(string cuitConsultado, string token, string sign)
        {
            try
            {
                // 1. Crear XML con el orden correcto
                string xmlRequest = CrearXMLConsulta(cuitConsultado, token, sign);

                // 2. Enviar request
                var content = new StringContent(xmlRequest, Encoding.UTF8, "text/xml");
                var request = new HttpRequestMessage(HttpMethod.Post, _urlWSP5)
                {
                    Content = content
                };
                request.Headers.Add("SOAPAction", "");
                request.Headers.Add("Accept", "text/xml");

                var response = await _httpClient.SendAsync(request);
                string xmlResponse = await response.Content.ReadAsStringAsync();

                // 3. Verificar si hay error
                if (xmlResponse.Contains("<soap:Fault>") || xmlResponse.Contains("<soapenv:Fault>"))
                {
                    string error = ProcesarError(xmlResponse);
                    throw new Exception($"Error de AFIP: {error}");
                }

                // 4. Procesar respuesta exitosa
                return ProcesarRespuestaExitosa(xmlResponse);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error al consultar persona: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Ejemplo de XML de respuesta exitosa (para referencia)
        /// </summary>
        public static string EjemploRespuestaExitosa = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"">
   <soapenv:Body>
      <ns2:getPersona_v2Response xmlns:ns2=""http://a5.soap.ws.server.puc.sr/"">
         <ns2:personaReturn>
            <idPersona>20365039829</idPersona>
            <tipoPersona>FISICA</tipoPersona>
            <nombre>JUAN PEREZ</nombre>
            <estadoClave>ACTIVO</estadoClave>
            <condicionIVA>4</condicionIVA>
            <domicilioFiscal>
               <direccion>AV CORRIENTES 1234</direccion>
               <localidad>CAPITAL FEDERAL</localidad>
               <codPostal>C1043AAX</codPostal>
            </domicilioFiscal>
         </ns2:personaReturn>
      </ns2:getPersona_v2Response>
   </soapenv:Body>
</soapenv:Envelope>";

        /// <summary>
        /// Ejemplo de XML de error (para referencia)
        /// </summary>
        public static string EjemploRespuestaError = @"<?xml version=""1.0"" encoding=""UTF-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
   <soap:Body>
      <soap:Fault>
         <faultcode>soap:Server</faultcode>
         <faultstring>No existe persona con ese Id</faultstring>
         <detail>
            <ns1:SRValidationException xmlns:ns1=""http://a5.soap.ws.server.puc.sr/""/>
         </detail>
      </soap:Fault>
   </soap:Body>
</soap:Envelope>";
    }
}










