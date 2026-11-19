using EstacionamientoWebApp.Models;
using EstacionamientoWebApp.Services.WSAA;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;


namespace EstacionamientoWebApp.Services
{
    public class AFIPService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<AFIPService> _logger;
        private readonly IHostEnvironment _hostEnvironment;
        private readonly WSAAService _wsaa;

        // Configuración básica
        private readonly string _entorno;
        private readonly string _urlWSAA;
        private readonly string _urlWSFE;
        private readonly string _urlWSP5;
        private readonly string _certificadoPath;
        private readonly string _certPassword;
        private readonly string _cuitServicio;
        private readonly int _timeout;
        private readonly bool _modoDemo;
        private readonly string _taStoragePath;
        private readonly bool _consultarPadron;

        // Caché en memoria para evitar múltiples solicitudes concurrentes al WSAA
        // Clave: CUIT + Entorno + Servicio, Valor: Token y timestamp
        private static readonly Dictionary<string, (AFIPToken Token, DateTime CachedAt)> _tokenCache = new();
        private static readonly SemaphoreSlim _tokenSemaphore = new SemaphoreSlim(1, 1);
        private static DateTime _lastCleanup = DateTime.MinValue;

        public AFIPService(
            IConfiguration configuration, 
            ILogger<AFIPService> logger, 
            IHostEnvironment hostEnvironment,
            WSAAService wsaa)
        {
            _configuration = configuration;
            _logger = logger;
            _hostEnvironment = hostEnvironment;
            _wsaa = wsaa;

            // ⚠️ Asegurar TLS 1.2 para todas las conexiones AFIP
            // Esto es crítico ya que AFIP/ARCA exigen TLS 1.2+ (TLS 1.0 y 1.1 están deshabilitados)
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

            _logger.LogInformation("[DEBUG] Iniciando AFIPService...");

            bool esProduccion = _configuration.GetValue<bool>("AFIP:EsProduccion");
            _modoDemo = _configuration.GetValue<bool>("AFIP:ModoDemo", false);
            _entorno = esProduccion ? "Produccion" : "Homologacion";

            _urlWSAA = _configuration[$"AFIP:{_entorno}:UrlWSAA"] ?? string.Empty;
            _urlWSFE = _configuration[$"AFIP:{_entorno}:UrlWSFE"] ?? string.Empty;
            _urlWSP5 = _configuration[$"AFIP:{_entorno}:UrlWSP5"] ?? string.Empty;
            _certificadoPath = _configuration[$"AFIP:{_entorno}:CertificadoPath"] ?? string.Empty;
            _certPassword = _configuration[$"AFIP:{_entorno}:Firma"] ?? string.Empty;
            _cuitServicio = _configuration[$"AFIP:{_entorno}:CuitServicioAFIP"] ?? string.Empty;
            _timeout = _configuration.GetValue<int>($"AFIP:{_entorno}:TimeOut", 30);
            _taStoragePath = _configuration[$"AFIP:{_entorno}:TAStoragePath"] ?? string.Empty;
            _consultarPadron = _configuration.GetValue<bool>("AFIP:ConsultarPadron", false);

            _logger.LogInformation($"[DEBUG] Entorno: {_entorno}");
            _logger.LogInformation($"[DEBUG] ModoDemo: {_modoDemo}");
            _logger.LogInformation($"[DEBUG] CertPath: {_certificadoPath}");
            _logger.LogInformation($"[DEBUG] CUIT: {_cuitServicio}");
            if (!string.IsNullOrWhiteSpace(_taStoragePath))
                _logger.LogInformation($"[DEBUG] TAStoragePath: {_taStoragePath}");

            try
            {
                if (!string.IsNullOrEmpty(_certificadoPath))
                {
                    var fullPath = Path.GetFullPath(_certificadoPath);
                    _logger.LogInformation($"[DEBUG] Intentando cargar certificado desde: {fullPath}");

                    if (!File.Exists(fullPath))
                    {
                        _logger.LogError($"[ERROR] No se encontró el archivo del certificado en: {fullPath}");
                    }
                    else
                    {
                        _logger.LogInformation("[DEBUG] Certificado encontrado correctamente.");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[ERROR] Problema verificando la ruta del certificado: {ex.Message}");
            }

            _logger.LogInformation("AFIPService inicializado en entorno: {Entorno}", _entorno);
        }

        // ================================
        // AUTENTICACIÓN (WSAA)
        // ================================

        /// <summary>
        /// Obtiene un token de acceso AFIP para el servicio especificado
        /// </summary>
        /// <param name="servicio">Servicio AFIP: "wsfe" para facturación electrónica, "ws_sr_padron_a5" para padrón. Por defecto "wsfe"</param>
        /// <returns>Token de acceso AFIP</returns>
        public async Task<AFIPToken> ObtenerTokenAsync(string servicio = "wsfe")
        {
            // Limpiar archivos de respuesta viejos periódicamente (una vez por hora)
            LimpiarRespuestasViejas();

            // Clave única para el caché: CUIT + Entorno + Servicio
            string cacheKey = $"{_cuitServicio}_{_entorno}_{servicio}";

            try
            {
                _logger.LogInformation("Obteniendo token AFIP para CUIT {Cuit} en entorno {Entorno} para servicio {Servicio}", _cuitServicio, _entorno, servicio);

                // 1. Verificar caché en memoria (solo si el token sigue siendo válido)
                await _tokenSemaphore.WaitAsync();
                try
                {
                    if (_tokenCache.TryGetValue(cacheKey, out var cached))
                    {
                        // Verificar si el token en caché sigue siendo válido (con margen de 5 minutos)
                        if (cached.Token.ExpirationTime > DateTime.Now.AddMinutes(5))
                        {
                            _logger.LogInformation("✓ Usando token desde caché en memoria (válido hasta {Exp})", cached.Token.ExpirationTime);
                            return cached.Token;
                        }
                        else
                        {
                            // Token expirado, remover del caché
                            _tokenCache.Remove(cacheKey);
                            _logger.LogInformation("Token en caché expirado, se solicitará uno nuevo");
                        }
                    }
                }
                finally
                {
                    _tokenSemaphore.Release();
                }

                // 2. Intentar cargar desde disco (más confiable que caché en memoria)
                if (TryLoadTokenFromDisk(servicio, out var tokenFromDisk))
                {
                    _logger.LogInformation("✓ Usando TA existente desde disco para servicio '{Servicio}' (válido hasta {Exp})", servicio, tokenFromDisk.ExpirationTime);
                    
                    // Actualizar caché en memoria
                    await _tokenSemaphore.WaitAsync();
                    try
                    {
                        _tokenCache[cacheKey] = (tokenFromDisk, DateTime.Now);
                    }
                    finally
                    {
                        _tokenSemaphore.Release();
                    }
                    
                    return tokenFromDisk;
                }

                // 3. Si no hay token válido, solicitar uno nuevo al WSAA
                // Usar semáforo para evitar múltiples solicitudes simultáneas
                await _tokenSemaphore.WaitAsync();
                try
                {
                    // Verificar nuevamente después de adquirir el lock (otro thread pudo haberlo creado)
                    if (_tokenCache.TryGetValue(cacheKey, out var cachedAfterLock))
                    {
                        if (cachedAfterLock.Token.ExpirationTime > DateTime.Now.AddMinutes(5))
                        {
                            _logger.LogInformation("✓ Token obtenido por otro thread, usando desde caché");
                            return cachedAfterLock.Token;
                        }
                    }

                    if (TryLoadTokenFromDisk(servicio, out var tokenFromDiskAfterLock))
                    {
                        _logger.LogInformation("✓ Token encontrado en disco después de lock para servicio '{Servicio}' (válido hasta {Exp})", servicio, tokenFromDiskAfterLock.ExpirationTime);
                        _tokenCache[cacheKey] = (tokenFromDiskAfterLock, DateTime.Now);
                        return tokenFromDiskAfterLock;
                    }

                    _logger.LogInformation("No se encontró token válido, solicitando uno nuevo al WSAA para servicio {Servicio}...", servicio);

                    if (!await VerificarCertificadoAsync(_certificadoPath, _certPassword))
                        throw new InvalidOperationException("Certificado inválido o no encontrado.");

                    string loginTicket = GenerarLoginTicketRequest(servicio);
                    string mensajeFirmado = FirmarMensaje(loginTicket, _certificadoPath, _certPassword);
                    string respuesta = await EnviarMensajeWSAAAsync(mensajeFirmado);

                    // Guardar respuesta solo si no es un error de "ya existe token"
                    // Esto evita generar múltiples archivos WSAA_Response innecesarios
                    bool esErrorTokenExistente = respuesta.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase) ||
                                                 respuesta.Contains("ya posee un TA valido", StringComparison.OrdinalIgnoreCase) ||
                                                 respuesta.Contains("ya posee un TA válido", StringComparison.OrdinalIgnoreCase);
                    
                    if (!esErrorTokenExistente)
                    {
                        GuardarRespuestaWSAAEnDisco(respuesta);
                    }
                    else
                    {
                        _logger.LogInformation("Respuesta WSAA indica token existente, no se guarda para evitar archivos duplicados");
                    }

                // Verificar si la respuesta contiene el error "alreadyAuthenticated"
                if (respuesta.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase) ||
                    respuesta.Contains("ya posee un TA valido", StringComparison.OrdinalIgnoreCase) ||
                    respuesta.Contains("ya posee un TA válido", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("WSAA indicó TA vigente (alreadyAuthenticated). Intentando usar TA desde disco...");
                    
                    // Intentar cargar desde disco (primer intento) - buscar token específico del servicio
                    if (TryLoadTokenFromDisk(servicio, out var tokenFromDisk1))
                    {
                        _logger.LogInformation("TA recuperado desde disco para servicio '{Servicio}' (válido hasta {Exp})", servicio, tokenFromDisk1.ExpirationTime);
                        return tokenFromDisk1;
                    }
                    
                    // Si no se encuentra, esperar un momento y volver a intentar (el token puede estar guardándose)
                    _logger.LogWarning("No se encontró TA en disco para servicio '{Servicio}'. Esperando 2 segundos y reintentando...", servicio);
                    await Task.Delay(2000);
                    
                    // Segundo intento
                    if (TryLoadTokenFromDisk(servicio, out var tokenFromDisk2))
                    {
                        _logger.LogInformation("TA recuperado desde disco en segundo intento para servicio '{Servicio}' (válido hasta {Exp})", servicio, tokenFromDisk2.ExpirationTime);
                        return tokenFromDisk2;
                    }
                    
                    // Si aún no se encuentra, intentar buscar en archivos WSAA_Response recientes
                    _logger.LogWarning("Intentando buscar token en archivos WSAA_Response recientes...");
                    if (TryLoadTokenFromWSAAResponseFiles(out var tokenFromWSAA))
                    {
                        _logger.LogInformation("✓ TA recuperado desde archivo WSAA_Response (válido hasta {Exp})", tokenFromWSAA.ExpirationTime);
                        
                        // Guardar el token encontrado para futuras consultas (buscar el archivo WSAA usado)
                        try
                        {
                            string certDir = GetTAStorageDir();
                            var archivoWSAA = Directory.GetFiles(certDir, "WSAA_Response_*.xml")
                                .OrderByDescending(f => File.GetCreationTime(f))
                                .FirstOrDefault(f => !File.ReadAllText(f, Encoding.UTF8)
                                    .Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase));
                            
                            if (archivoWSAA != null)
                            {
                                string contenidoWSAA = File.ReadAllText(archivoWSAA, Encoding.UTF8);
                                GuardarTAEnDisco(contenidoWSAA, servicio);
                                _logger.LogInformation("Token guardado en TA_{Servicio}.xml y TA_{Servicio}_clean.xml para futuras consultas", servicio);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "No se pudo guardar el token encontrado, pero se usará en esta sesión");
                        }
                        
                        return tokenFromWSAA;
                    }
                    
                    // Si aún no se encuentra, verificar si hay un error SOAP diferente
                    if (respuesta.Contains("soapenv:Fault", StringComparison.OrdinalIgnoreCase))
                    {
                        string faultString = Regex.Match(respuesta, @"<faultstring>(.*?)</faultstring>").Groups[1].Value;
                        if (string.IsNullOrEmpty(faultString))
                            faultString = Regex.Match(respuesta, @"<faultstring[^>]*>(.*?)</faultstring>", RegexOptions.IgnoreCase).Groups[1].Value;
                        if (string.IsNullOrEmpty(faultString))
                            faultString = "El CEE ya posee un TA válido para el acceso al WSN solicitado";
                        
                        string certDir = GetTAStorageDir();
                        _logger.LogError("No se pudo cargar un TA válido desde disco después de múltiples intentos. AFIP reporta: {Fault}", faultString);
                        _logger.LogError("Directorio de tokens: {Dir}", certDir);
                        _logger.LogError("Solución: Use el botón 'Limpiar Tokens' en la página de configuración AFIP (/ConfiguracionAFIP) para forzar la obtención de un nuevo token.");
                        
                        throw new InvalidOperationException($"Error de AFIP: {faultString}\n\n" +
                            $"AFIP tiene un token válido activo en sus servidores, pero el sistema no puede encontrarlo localmente.\n\n" +
                            $"SOLUCIÓN RECOMENDADA:\n" +
                            $"1. Vaya a la página de Configuración AFIP (/ConfiguracionAFIP)\n" +
                            $"2. Haga clic en el botón 'Limpiar Tokens'\n" +
                            $"3. Espere unos minutos y vuelva a intentar la consulta\n\n" +
                            $"ALTERNATIVA: Si el problema persiste, espere a que expire el token en AFIP (puede tardar hasta 12 horas desde su creación).");
                    }
                    
                    // Si no es un error SOAP, lanzar excepción con mensaje descriptivo
                    string certDir2 = GetTAStorageDir();
                    _logger.LogError("No se pudo cargar un TA válido desde disco después de múltiples intentos.");
                    _logger.LogError("Directorio de tokens: {Dir}", certDir2);
                    throw new InvalidOperationException("Error de AFIP: El CEE ya posee un TA válido para el acceso al WSN solicitado.\n\n" +
                        "AFIP tiene un token válido activo en sus servidores, pero el sistema no puede encontrarlo localmente.\n\n" +
                        "SOLUCIÓN RECOMENDADA:\n" +
                        "1. Vaya a la página de Configuración AFIP (/ConfiguracionAFIP)\n" +
                        "2. Haga clic en el botón 'Limpiar Tokens'\n" +
                        "3. Espere unos minutos y vuelva a intentar la consulta\n\n" +
                        "ALTERNATIVA: Si el problema persiste, espere a que expire el token en AFIP (puede tardar hasta 12 horas desde su creación).");
                }
                
                // Verificar otros errores SOAP (incluyendo "Computador no autorizado")
                if (respuesta.Contains("soapenv:Fault", StringComparison.OrdinalIgnoreCase))
                {
                    string faultString = Regex.Match(respuesta, @"<faultstring>(.*?)</faultstring>").Groups[1].Value;
                    if (string.IsNullOrEmpty(faultString))
                        faultString = Regex.Match(respuesta, @"<faultstring[^>]*>(.*?)</faultstring>", RegexOptions.IgnoreCase).Groups[1].Value;
                    if (string.IsNullOrEmpty(faultString))
                        faultString = "Error desconocido de AFIP";
                    
                    // Si es un error de autorización, lanzar excepción específica
                    if (faultString.Contains("Computador no autorizado", StringComparison.OrdinalIgnoreCase) ||
                        faultString.Contains("no autorizado", StringComparison.OrdinalIgnoreCase))
                    {
                        string mensajeDetallado = $"❌ ERROR DE AUTORIZACIÓN AFIP:\n\n" +
                            $"El certificado (CUIT {_cuitServicio}) NO está asociado al servicio '{servicio}'.\n\n" +
                            $"📋 CAUSA:\n" +
                            $"El certificado no está asociado al Web Service en el Administrador de Relaciones de AFIP.\n\n" +
                            $"🔧 SOLUCIÓN:\n" +
                            $"1. Ingresá al portal de AFIP (https://www.afip.gob.ar/)\n" +
                            $"2. Accedé al Administrador de Relaciones\n" +
                            $"3. Asociá tu certificado al servicio correspondiente\n\n" +
                            $"⚠️ NOTA: Si no ves los servicios nacionales en el listado, es un problema de configuración en ARCA/AFIP.\n" +
                            $"Contactá a ARCA o AFIP para que habiliten los servicios para tu CUIT.\n\n" +
                            $"Mensaje de AFIP: {faultString}";
                        
                        _logger.LogError("⚠️ ERROR CRÍTICO: Computador no autorizado para servicio {Servicio}. " +
                            "Certificado CUIT {Cuit} no está asociado al servicio.", servicio, _cuitServicio);
                        
                        throw new UnauthorizedAccessException(mensajeDetallado);
                    }
                    
                    throw new InvalidOperationException($"Error de AFIP: {faultString}");
                }

                    // Guardar TA recibido y su versión limpia
                    GuardarTAEnDisco(respuesta, servicio);

                    var token = ProcesarRespuestaWSAA(respuesta);
                    token.Cuit = _cuitServicio;
                    
                    // Actualizar caché en memoria con el nuevo token
                    _tokenCache[cacheKey] = (token, DateTime.Now);
                    
                    _logger.LogInformation("✓ Token AFIP obtenido exitosamente y guardado en caché (válido hasta {Exp})", token.ExpirationTime);
                    return token;
                }
                finally
                {
                    _tokenSemaphore.Release();
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Re-lanzar excepciones de autorización sin modificar (ya tienen el mensaje detallado)
                throw;
            }
            catch (Exception ex)
            {
                // Si es un error de autorización, re-lanzar con mensaje mejorado
                if (ex.Message.Contains("Computador no autorizado", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("no autorizado", StringComparison.OrdinalIgnoreCase) && 
                    !ex.Message.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase))
                {
                    string mensajeMejorado = $"❌ ERROR DE AUTORIZACIÓN AFIP:\n\n" +
                        $"El certificado (CUIT {_cuitServicio}) NO está asociado al servicio '{servicio}'.\n\n" +
                        $"📋 CAUSA:\n" +
                        $"El certificado no está asociado al Web Service en el Administrador de Relaciones de AFIP.\n\n" +
                        $"🔧 SOLUCIÓN:\n" +
                        $"1. Ingresá al portal de AFIP (https://www.afip.gob.ar/)\n" +
                        $"2. Accedé al Administrador de Relaciones\n" +
                        $"3. Asociá tu certificado al servicio correspondiente\n\n" +
                        $"⚠️ NOTA: Si no ves los servicios nacionales en el listado, es un problema de configuración en ARCA/AFIP.\n" +
                        $"Contactá a ARCA o AFIP para que habiliten los servicios para tu CUIT.\n\n" +
                        $"Error original: {ex.Message}";
                    
                    _logger.LogError("⚠️ ERROR CRÍTICO: Computador no autorizado para servicio {Servicio}. " +
                        "Certificado CUIT {Cuit} no está asociado al servicio.", servicio, _cuitServicio);
                    
                    throw new UnauthorizedAccessException(mensajeMejorado, ex);
                }
                
                // Si AFIP informa TA vigente, intentar reutilizar el TA del disco
                if (ex.Message.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("ya posee un TA valido", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("ya posee un TA válido", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Excepción contiene alreadyAuthenticated. Intentando usar TA desde disco...");
                    if (TryLoadTokenFromDisk(servicio, out var tokenFromDisk))
                    {
                        _logger.LogInformation("TA recuperado desde disco para servicio '{Servicio}' (válido hasta {Exp})", servicio, tokenFromDisk.ExpirationTime);
                        
                        // Actualizar caché
                        await _tokenSemaphore.WaitAsync();
                        try
                        {
                            _tokenCache[cacheKey] = (tokenFromDisk, DateTime.Now);
                        }
                        finally
                        {
                            _tokenSemaphore.Release();
                        }
                        
                        return tokenFromDisk;
                    }
                    _logger.LogError("No se pudo cargar un TA válido desde disco a pesar de alreadyAuthenticated.");
                }
                _logger.LogError(ex, "Error al obtener token AFIP");
                throw;
            }
        }

        private string FirmarMensaje(string xml, string rutaCertificado, string password)
        {
            _logger.LogInformation("🔐 Firmando CMS (AFIP compatible)");

            // 1) Cargar certificado con clave privada
            var cert = new X509Certificate2(
                rutaCertificado,
                password,
                X509KeyStorageFlags.MachineKeySet |
                X509KeyStorageFlags.Exportable |
                X509KeyStorageFlags.PersistKeySet
            );

            if (!cert.HasPrivateKey)
                throw new Exception("El certificado no tiene clave privada");

            // 2) XML exacto, sin BOM, sin normalización
            string rawXml = xml.Trim();
            byte[] data = Encoding.ASCII.GetBytes(rawXml);

            // 3) ContentInfo Detached
            ContentInfo contentInfo = new ContentInfo(data);
            SignedCms signedCms = new SignedCms(contentInfo, detached: true);

            // 4) Crear signer compatible con AFIP
            CmsSigner signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert);

            signer.IncludeOption = X509IncludeOption.EndCertOnly;

            // 💥 LA CLAVE EXACTA — AFIP no acepta parámetros ASN.1
            signer.DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"); // SHA-256 OID
            signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.UtcNow));

            // 5) Firmar (sin parámetros)
            signedCms.ComputeSignature(signer);

            byte[] encoded = signedCms.Encode();

            string cmsBase64 = Convert.ToBase64String(encoded, Base64FormattingOptions.None);

            // 6) Armar SOAP final
            string soap = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soapenv:Header/>
  <soapenv:Body>
    <loginCms>
      <in0>{cmsBase64}</in0>
    </loginCms>
  </soapenv:Body>
</soapenv:Envelope>";

            _logger.LogInformation("✓ CMS firmado correctamente para WSAA");
            return soap;
        }



        private string GenerarLoginTicketRequest(string service)
        {
            uint uniqueId = (uint)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

            DateTime gen = DateTime.Now.AddMinutes(-1);
            DateTime exp = DateTime.Now.AddMinutes(5);

            return
                $"<loginTicketRequest version=\"1.0\">" +
                $"<header>" +
                $"<uniqueId>{uniqueId}</uniqueId>" +
                $"<generationTime>{gen:yyyy-MM-ddTHH:mm:sszzz}</generationTime>" +
                $"<expirationTime>{exp:yyyy-MM-ddTHH:mm:sszzz}</expirationTime>" +
                $"</header>" +
                $"<service>{service}</service>" +
                $"</loginTicketRequest>";
        }


        private async Task<string> EnviarMensajeWSAAAsync(string xmlFirmado)
        {
            try
            {
                _logger.LogInformation("📡 Enviando solicitud WSAA: {Url}", _urlWSAA);
                
                // ✅ 4. Verificar que la URL sea correcta
                if (string.IsNullOrWhiteSpace(_urlWSAA))
                {
                    throw new InvalidOperationException("URL de WSAA no configurada. Verifique appsettings.json");
                }
                
                // Verificar que la URL corresponda al ambiente configurado
                bool esProduccion = _entorno == "Produccion";
                bool urlEsProduccion = _urlWSAA.Contains("wsaa.afip.gov.ar") && !_urlWSAA.Contains("wsaahomo");
                bool urlEsHomologacion = _urlWSAA.Contains("wsaahomo.afip.gov.ar");
                
                if (esProduccion && !urlEsProduccion)
                {
                    _logger.LogWarning("⚠️ ADVERTENCIA: Ambiente configurado es PRODUCCIÓN pero la URL parece ser de HOMOLOGACIÓN: {Url}", _urlWSAA);
                }
                else if (!esProduccion && !urlEsHomologacion)
                {
                    _logger.LogWarning("⚠️ ADVERTENCIA: Ambiente configurado es HOMOLOGACIÓN pero la URL parece ser de PRODUCCIÓN: {Url}", _urlWSAA);
                }
                else
                {
                    _logger.LogInformation("✓ URL WSAA coincide con el ambiente configurado: {Ambiente}", _entorno);
                }

                // ✅ 3.1. TLS 1.2 es obligatorio desde 2023
                // TLS 1.2 ya configurado globalmente, pero lo mantenemos aquí como respaldo
                ServicePointManager.SecurityProtocol = ServicePointManager.SecurityProtocol | SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                
                _logger.LogDebug("✓ Protocolo de seguridad configurado: {Protocol}", ServicePointManager.SecurityProtocol);
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeout) };

                var request = new HttpRequestMessage(HttpMethod.Post, _urlWSAA)
                {
                    Content = new StringContent(xmlFirmado, Encoding.UTF8, "text/xml")
                };

                request.Headers.Clear();
                request.Headers.Add("SOAPAction", "");
                request.Headers.Add("Accept", "text/xml");

                var response = await client.SendAsync(request);
                string content = await response.Content.ReadAsStringAsync();

                // Verificar si la respuesta contiene un error SOAP (aunque el HTTP sea 200 o 500)
                if (content.Contains("soapenv:Fault", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("ya posee un TA valido", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("Computador no autorizado", StringComparison.OrdinalIgnoreCase) ||
                    content.Contains("no autorizado", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("WSAA devolvió un error SOAP en la respuesta (HTTP {StatusCode}): {Content}", response.StatusCode, content);
                    
                    // Extraer el mensaje de error
                    string faultString = Regex.Match(content, @"<faultstring>(.*?)</faultstring>").Groups[1].Value;
                    if (string.IsNullOrEmpty(faultString))
                        faultString = Regex.Match(content, @"<faultstring[^>]*>(.*?)</faultstring>", RegexOptions.IgnoreCase).Groups[1].Value;
                    
                    // Detectar específicamente el error "Computador no autorizado"
                    if (content.Contains("Computador no autorizado", StringComparison.OrdinalIgnoreCase) ||
                        content.Contains("no autorizado", StringComparison.OrdinalIgnoreCase) && 
                        !content.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase))
                    {
                        string mensajeDetallado = $"❌ ERROR DE AUTORIZACIÓN AFIP:\n\n" +
                            $"El certificado (CUIT {_cuitServicio}) NO está asociado al servicio solicitado.\n\n" +
                            $"📋 CAUSA PROBABLE:\n" +
                            $"El certificado no está asociado al Web Service en el Administrador de Relaciones de AFIP.\n\n" +
                            $"🔧 SOLUCIÓN:\n" +
                            $"1. Ingresá al portal de AFIP (https://www.afip.gob.ar/)\n" +
                            $"2. Accedé al Administrador de Relaciones\n" +
                            $"3. Asociá tu certificado al servicio correspondiente (WSFEv1, WSAA, etc.)\n\n" +
                            $"⚠️ NOTA: Si no ves los servicios nacionales en el listado, es un problema de configuración en ARCA/AFIP.\n" +
                            $"Contactá a ARCA o AFIP para que habiliten los servicios para tu CUIT.\n\n" +
                            $"Mensaje de AFIP: {faultString}";
                        
                        _logger.LogError("⚠️ ERROR CRÍTICO: Computador no autorizado. Certificado CUIT {Cuit} no está asociado al servicio. " +
                            "Esto indica que el certificado no está vinculado en el Administrador de Relaciones de AFIP.", _cuitServicio);
                        
                        throw new UnauthorizedAccessException(mensajeDetallado);
                    }
                    
                    // Si es "alreadyAuthenticated", no lanzar excepción aquí, dejar que el método ObtenerTokenAsync maneje el error
                    if (!content.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase))
                    {
                        if (string.IsNullOrEmpty(faultString))
                            faultString = "Error desconocido de AFIP";
                        throw new HttpRequestException($"Error de AFIP: {faultString}");
                    }
                    // Para "alreadyAuthenticated", retornar el contenido para que ObtenerTokenAsync lo maneje
                    return content;
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Error HTTP {response.StatusCode}: {content}");

                _logger.LogDebug("Respuesta WSAA recibida correctamente");
                return content;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al enviar mensaje WSAA ({Url})", _urlWSAA);
                throw;
            }
        }

        private AFIPToken ProcesarRespuestaWSAA(string respuesta)
        {
            try
            {
                _logger.LogDebug("Procesando respuesta WSAA. Longitud: {Length} caracteres", respuesta.Length);

                // Verificar si la respuesta contiene un error SOAP
                if (respuesta.Contains("soapenv:Fault", StringComparison.OrdinalIgnoreCase) ||
                    respuesta.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase))
                {
                    string faultString = Regex.Match(respuesta, @"<faultstring>(.*?)</faultstring>").Groups[1].Value;
                    if (string.IsNullOrEmpty(faultString))
                        faultString = Regex.Match(respuesta, @"<faultstring[^>]*>(.*?)</faultstring>", RegexOptions.IgnoreCase).Groups[1].Value;
                    throw new InvalidOperationException($"Error de AFIP: {faultString}");
                }

                // Desescapar HTML si es necesario
                string respuestaLimpia = respuesta;
                if (respuestaLimpia.Contains("&lt;") || respuestaLimpia.Contains("&gt;"))
                {
                    respuestaLimpia = HtmlDecode(respuestaLimpia);
                    _logger.LogDebug("Respuesta desescapada");
                }

                // Intentar extraer usando XmlDocument (más robusto)
                string token = string.Empty;
                string sign = string.Empty;
                string expiration = string.Empty;

                try
                {
                    var xmlDoc = new XmlDocument();
                    xmlDoc.LoadXml(respuestaLimpia);

                    // Buscar loginTicketResponse
                    var nsManager = new XmlNamespaceManager(xmlDoc.NameTable);
                    nsManager.AddNamespace("soap", "http://schemas.xmlsoap.org/soap/envelope/");
                    nsManager.AddNamespace("ns", "");

                    var loginTicketResponse = xmlDoc.SelectSingleNode("//loginTicketResponse", nsManager) ??
                                             xmlDoc.SelectSingleNode("//*[local-name()='loginTicketResponse']");

                    if (loginTicketResponse != null)
                    {
                        var tokenNode = loginTicketResponse.SelectSingleNode(".//token", nsManager) ??
                                       loginTicketResponse.SelectSingleNode(".//*[local-name()='token']");
                        var signNode = loginTicketResponse.SelectSingleNode(".//sign", nsManager) ??
                                      loginTicketResponse.SelectSingleNode(".//*[local-name()='sign']");
                        var expirationNode = loginTicketResponse.SelectSingleNode(".//expirationTime", nsManager) ??
                                           loginTicketResponse.SelectSingleNode(".//*[local-name()='expirationTime']");

                        token = tokenNode?.InnerText ?? string.Empty;
                        sign = signNode?.InnerText ?? string.Empty;
                        expiration = expirationNode?.InnerText ?? string.Empty;
                    }
                }
                catch (XmlException xmlEx)
                {
                    _logger.LogWarning(xmlEx, "Error al parsear XML, intentando con regex");
                }

                // Si no se encontró con XmlDocument, intentar con regex (más permisivo con namespaces)
                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(sign))
                {
                    // Buscar con regex tolerante a namespaces
                    var tokenMatch = Regex.Match(respuestaLimpia, @"<(?:[^:>]+:)?token[^>]*>(.*?)</(?:[^:>]+:)?token>", RegexOptions.IgnoreCase);
                    var signMatch = Regex.Match(respuestaLimpia, @"<(?:[^:>]+:)?sign[^>]*>(.*?)</(?:[^:>]+:)?sign>", RegexOptions.IgnoreCase);
                    var expirationMatch = Regex.Match(respuestaLimpia, @"<(?:[^:>]+:)?expirationTime[^>]*>(.*?)</(?:[^:>]+:)?expirationTime>", RegexOptions.IgnoreCase);

                    if (tokenMatch.Success && !string.IsNullOrEmpty(tokenMatch.Groups[1].Value))
                        token = tokenMatch.Groups[1].Value;
                    if (signMatch.Success && !string.IsNullOrEmpty(signMatch.Groups[1].Value))
                        sign = signMatch.Groups[1].Value;
                    if (expirationMatch.Success && !string.IsNullOrEmpty(expirationMatch.Groups[1].Value))
                        expiration = expirationMatch.Groups[1].Value;
                }

                if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(sign))
                {
                    // Loggear parte de la respuesta para debugging (sin exponer datos sensibles)
                    string preview = respuestaLimpia.Length > 500 ? respuestaLimpia.Substring(0, 500) + "..." : respuestaLimpia;
                    _logger.LogError("No se pudo extraer token o sign de la respuesta WSAA. Preview: {Preview}", preview);
                    throw new InvalidOperationException("La respuesta de WSAA no contiene token o sign válidos. Verifique los logs para más detalles.");
                }

                DateTime.TryParse(expiration, out DateTime expira);
                expira = expira == default ? DateTime.Now.AddHours(12) : expira;

                _logger.LogInformation("Token extraído exitosamente. Expira: {Exp}", expira);

                return new AFIPToken
                {
                    Token = token,
                    Sign = sign,
                    ExpirationTime = expira,
                    Cuit = _cuitServicio
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando respuesta WSAA");
                throw;
            }
        }

        //}

        // ================================
        // Persistencia y carga de TA
        // ================================
        
        /// <summary>
        /// Guarda TODAS las respuestas de WSAA (incluso errores) para debugging
        /// </summary>
        private void GuardarRespuestaWSAAEnDisco(string respuestaWsaa)
        {
            try
            {
                string certDir = GetTAStorageDir();
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string responsePath = Path.Combine(certDir, $"WSAA_Response_{timestamp}.xml");
                
                _logger.LogInformation("[DEBUG] Guardando respuesta WSAA en: {Path}", responsePath);
                File.WriteAllText(responsePath, respuestaWsaa, Encoding.UTF8);
                _logger.LogInformation("[DEBUG] Respuesta WSAA guardada exitosamente");
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "No se pudo guardar respuesta WSAA en disco");
            }
        }

        /// <summary>
        /// Guarda el TA (Token de Acceso) en disco con nombre específico por servicio
        /// Cada servicio (wsfe, ws_sr_padron_a5, etc.) tiene su propio archivo TA
        /// </summary>
        private void GuardarTAEnDisco(string respuestaWsaa, string servicio)
        {
            try
            {
                string certDir = GetTAStorageDir();

                // Normalizar nombre del servicio para usar en el archivo
                string nombreServicio = servicio.ToLower().Replace("/", "_").Replace("\\", "_");
                string taPath = Path.Combine(certDir, $"TA_{nombreServicio}.xml");
                string taCleanPath = Path.Combine(certDir, $"TA_{nombreServicio}_clean.xml");

                _logger.LogInformation("Guardando TA para servicio '{Servicio}' en: {Path}", servicio, taPath);

                // 1) Guardar SIEMPRE la respuesta completa (para debug)
                File.WriteAllText(taPath, respuestaWsaa, Encoding.UTF8);

                // 2) Parsear el SOAP
                var soap = System.Xml.Linq.XDocument.Parse(respuestaWsaa);

                // 3) Buscar el nodo <loginCmsReturn> SIN preocuparnos por el namespace
                var loginCmsReturn = soap
                    .Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "loginCmsReturn");

                if (loginCmsReturn == null)
                {
                    _logger.LogWarning("No se encontró el nodo loginCmsReturn en la respuesta WSAA.");
                    return;
                }

                // 4) El contenido del loginCmsReturn viene ESCAPADO (&lt;loginTicketResponse&gt;...)
                string innerXmlEscapado = loginCmsReturn.Value;

                // 5) Desescapar (&lt; → <, &gt; → >, etc.)
                string innerXml = WebUtility.HtmlDecode(innerXmlEscapado);

                // 6) Parsear ahora sí el <loginTicketResponse> real
                var loginTicketDoc = System.Xml.Linq.XDocument.Parse(innerXml);

                // 7) Guardar SOLO el TA limpio
                loginTicketDoc.Save(taCleanPath);

                _logger.LogInformation("✓ TA limpio guardado correctamente en: {Path}", taCleanPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al guardar TA limpio para servicio {Servicio}", servicio);
            }
        }



        /// <summary>
        /// Intenta cargar un token desde disco para un servicio específico
        /// </summary>
        private bool TryLoadTokenFromDisk(string servicio, out AFIPToken token)
        {
            token = new AFIPToken();
            try
            {
                string certDir = GetTAStorageDir();
                
                // Normalizar nombre del servicio para buscar el archivo correcto
                string nombreServicio = servicio.ToLower().Replace("/", "_").Replace("\\", "_");
                string cleanPath = Path.Combine(certDir, $"TA_{nombreServicio}_clean.xml");
                string taPath = Path.Combine(certDir, $"TA_{nombreServicio}.xml");

                _logger.LogInformation("[DEBUG] Intentando cargar TA desde disco. Directorio: {Dir}", certDir);
                _logger.LogInformation("[DEBUG] Ruta completa: {FullPath}", Path.GetFullPath(certDir));

                // Verificar si el directorio existe
                if (!Directory.Exists(certDir))
                {
                    _logger.LogWarning("[DEBUG] El directorio de tokens no existe: {Dir}", certDir);
                    return false;
                }

                // Listar todos los archivos en el directorio para debugging
                try
                {
                    var archivos = Directory.GetFiles(certDir, "TA*.xml");
                    _logger.LogInformation("[DEBUG] Archivos TA encontrados en directorio: {Count}", archivos.Length);
                    foreach (var archivo in archivos)
                    {
                        _logger.LogInformation("[DEBUG] - {Archivo}", Path.GetFileName(archivo));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DEBUG] No se pudieron listar archivos en el directorio");
                }

                string? xmlContent = null;
                string archivoUsado = "";
                
                _logger.LogInformation("[DEBUG] Buscando TA para servicio '{Servicio}': {CleanPath} o {TaPath}", servicio, cleanPath, taPath);
                
                if (File.Exists(cleanPath))
                {
                    xmlContent = File.ReadAllText(cleanPath, Encoding.UTF8);
                    archivoUsado = $"TA_{nombreServicio}_clean.xml";
                    _logger.LogInformation("[DEBUG] Cargado {Archivo}, tamaño: {Size} bytes", archivoUsado, xmlContent?.Length ?? 0);
                }
                else if (File.Exists(taPath))
                {
                    var raw = File.ReadAllText(taPath, Encoding.UTF8);
                    archivoUsado = $"TA_{nombreServicio}.xml";
                    _logger.LogInformation("[DEBUG] Cargado {Archivo}, tamaño: {Size} bytes", archivoUsado, raw.Length);
                    int s = raw.IndexOf("<loginTicketResponse", StringComparison.OrdinalIgnoreCase);
                    int e = raw.IndexOf("</loginTicketResponse>", StringComparison.OrdinalIgnoreCase);
                    if (s >= 0 && e > s)
                    {
                        xmlContent = raw.Substring(s, e - s + "</loginTicketResponse>".Length);
                        while (xmlContent.Contains("&lt;") && xmlContent.Contains("&gt;"))
                        {
                            xmlContent = HtmlDecode(xmlContent);
                        }
                        _logger.LogInformation("[DEBUG] Extraído loginTicketResponse, tamaño: {Size} bytes", xmlContent?.Length ?? 0);
                    }
                    else
                    {
                        _logger.LogWarning("[DEBUG] No se encontró <loginTicketResponse> en {Archivo}", archivoUsado);
                    }
                }
                else
                {
                    _logger.LogWarning("[DEBUG] No se encontraron archivos TA para servicio '{Servicio}' en {Dir}", servicio, certDir);
                    _logger.LogWarning("[DEBUG] Buscados: {CleanPath} y {TaPath}", cleanPath, taPath);
                    return false;
                }

                if (string.IsNullOrWhiteSpace(xmlContent))
                {
                    _logger.LogWarning("[DEBUG] xmlContent está vacío después de procesar desde {Archivo}", archivoUsado);
                    return false;
                }

                // Intentar parsear el XML con mejor manejo de errores
                XmlDocument xml;
                try
                {
                    xml = new XmlDocument();
                    xml.LoadXml(xmlContent);
                }
                catch (XmlException xmlEx)
                {
                    _logger.LogError(xmlEx, "[ERROR] Error al parsear XML del token desde {Archivo}. Contenido (primeros 200 chars): {Preview}", 
                        archivoUsado, xmlContent?.Substring(0, Math.Min(200, xmlContent?.Length ?? 0)) ?? "null");
                    return false;
                }

                string tok = xml.GetElementsByTagName("token")[0]?.InnerText ?? string.Empty;
                string sig = xml.GetElementsByTagName("sign")[0]?.InnerText ?? string.Empty;
                string exp = xml.GetElementsByTagName("expirationTime")[0]?.InnerText ?? string.Empty;

                if (string.IsNullOrEmpty(tok) || string.IsNullOrEmpty(sig) || string.IsNullOrEmpty(exp))
                {
                    _logger.LogWarning("[DEBUG] Faltan campos en el TA desde {Archivo}. Token: {HasToken}, Sign: {HasSign}, Exp: {HasExp}", 
                        archivoUsado, !string.IsNullOrEmpty(tok), !string.IsNullOrEmpty(sig), !string.IsNullOrEmpty(exp));
                    return false;
                }

                if (!DateTime.TryParse(exp, out var expDt))
                {
                    _logger.LogWarning("[DEBUG] No se pudo parsear la fecha de expiración: {Exp}", exp);
                    return false;
                }

                if (expDt <= DateTime.Now.AddMinutes(1))
                {
                    _logger.LogWarning("[DEBUG] TA expirado o por expirar. Expira: {Exp}, Ahora: {Ahora}", expDt, DateTime.Now);
                    return false; // vencido o por vencer muy pronto
                }

                _logger.LogInformation("[DEBUG] ✓ TA válido encontrado en {Archivo}. Expira: {Exp}", archivoUsado, expDt);
                token = new AFIPToken
                {
                    Token = tok,
                    Sign = sig,
                    ExpirationTime = expDt,
                    Cuit = _cuitServicio
                };
                return true;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "[ERROR] Excepción al intentar cargar TA desde disco: {Mensaje}", e.Message);
                return false;
            }
        }

        /// <summary>
        /// Intenta cargar un token desde archivos WSAA_Response recientes
        /// </summary>
        private bool TryLoadTokenFromWSAAResponseFiles(out AFIPToken token)
        {
            token = new AFIPToken();
            try
            {
                string certDir = GetTAStorageDir();
                
                if (!Directory.Exists(certDir))
                {
                    return false;
                }

                // Buscar archivos WSAA_Response ordenados por fecha de creación (más recientes primero)
                var archivosWSAA = Directory.GetFiles(certDir, "WSAA_Response_*.xml")
                    .OrderByDescending(f => File.GetCreationTime(f))
                    .Take(5) // Revisar solo los 5 más recientes
                    .ToList();

                if (archivosWSAA.Count == 0)
                {
                    _logger.LogInformation("[DEBUG] No se encontraron archivos WSAA_Response para buscar token");
                    return false;
                }

                _logger.LogInformation("[DEBUG] Buscando token en {Count} archivos WSAA_Response recientes", archivosWSAA.Count);

                foreach (var archivo in archivosWSAA)
                {
                    try
                    {
                        string contenido = File.ReadAllText(archivo, Encoding.UTF8);
                        
                        // Verificar que no sea un error (no debe contener "alreadyAuthenticated" o "Fault")
                        if (contenido.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase) ||
                            contenido.Contains("soapenv:Fault", StringComparison.OrdinalIgnoreCase) ||
                            contenido.Contains("soap:Fault", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("[DEBUG] Archivo {Archivo} contiene error, saltando", Path.GetFileName(archivo));
                            continue;
                        }

                        // Intentar extraer el token de la respuesta
                        var tokenExtraido = ProcesarRespuestaWSAA(contenido);
                        if (tokenExtraido != null && 
                            !string.IsNullOrEmpty(tokenExtraido.Token) && 
                            !string.IsNullOrEmpty(tokenExtraido.Sign) &&
                            tokenExtraido.ExpirationTime > DateTime.Now.AddMinutes(1))
                        {
                            token = tokenExtraido;
                            token.Cuit = _cuitServicio;
                            _logger.LogInformation("[DEBUG] ✓ Token válido encontrado en {Archivo}. Expira: {Exp}", 
                                Path.GetFileName(archivo), token.ExpirationTime);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[DEBUG] Error al procesar archivo {Archivo}", Path.GetFileName(archivo));
                        continue;
                    }
                }

                _logger.LogInformation("[DEBUG] No se encontró token válido en archivos WSAA_Response");
                return false;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "[ERROR] Excepción al intentar cargar TA desde archivos WSAA_Response: {Mensaje}", e.Message);
                return false;
            }
        }

        private string GetTAStorageDir()
        {
            try
            {
                string dir = !string.IsNullOrWhiteSpace(_taStoragePath)
                    ? _taStoragePath
                    : Path.Combine(_hostEnvironment.ContentRootPath, "Certificados");
                
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    _logger.LogInformation("[DEBUG] Directorio de tokens creado: {Dir}", dir);
                }
                
                string fullPath = Path.GetFullPath(dir);
                _logger.LogInformation("[DEBUG] Directorio de tokens (ruta completa): {FullPath}", fullPath);
                return dir;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al obtener TAStorageDir, usando fallback");
                // Fallback seguro: usar ContentRootPath o AppContext.BaseDirectory
                string fallback = Path.Combine(_hostEnvironment?.ContentRootPath ?? AppContext.BaseDirectory, "Certificados");
                if (!Directory.Exists(fallback)) Directory.CreateDirectory(fallback);
                string fallbackFullPath = Path.GetFullPath(fallback);
                _logger.LogInformation("[DEBUG] Usando directorio fallback (ruta completa): {FullPath}", fallbackFullPath);
                return fallback;
            }
        }

        // ================================
        // VERIFICACIÓN DE CERTIFICADO
        // ================================
        public Task<bool> VerificarCertificadoAsync(string rutaCertificado, string password)
        {
            try
            {
                if (string.IsNullOrEmpty(rutaCertificado))
                {
                    _logger.LogError("Ruta de certificado no configurada. Verifique appsettings.json");
                    return Task.FromResult(false);
                }

                // Resolver ruta relativa o absoluta
                string rutaCompleta = Path.IsPathRooted(rutaCertificado) 
                    ? rutaCertificado 
                    : Path.Combine(Directory.GetCurrentDirectory(), rutaCertificado);

                if (!File.Exists(rutaCompleta))
                {
                    _logger.LogError("Certificado no encontrado en: {Ruta}. Verifique que el archivo existe en la carpeta Certificados.", rutaCompleta);
                    _logger.LogInformation("Para trabajar en modo demo sin certificado, active 'ModoDemo: true' en appsettings.json");
                    return Task.FromResult(false);
                }

                var cert = new X509Certificate2(rutaCompleta, password, X509KeyStorageFlags.Exportable);
                
                // ✅ 1.3. Verificar que el certificado no esté expirado
                if (cert.NotAfter < DateTime.Now)
                {
                    _logger.LogError("❌ Certificado expirado: {Fecha}. WSAA no devolverá token con certificado vencido.", cert.NotAfter);
                    return Task.FromResult(false);
                }
                
                // ✅ 1.2. Verificar que el certificado tenga clave privada
                if (!cert.HasPrivateKey)
                {
                    _logger.LogError("❌ ERROR CRÍTICO: El certificado NO tiene clave privada.\n\n" +
                        "El archivo .pfx no contiene la clave privada necesaria para firmar el CMS.\n\n" +
                        "🔧 SOLUCIÓN:\n" +
                        "1. Verificá que el certificado se exportó correctamente con la clave privada\n" +
                        "2. Si convertiste de .crt a .pfx, asegurate de incluir la clave privada (.key)\n" +
                        "3. Regenerá el certificado desde AFIP si es necesario\n\n" +
                        "Sin clave privada, WSAA NO puede validar la firma y NO devolverá token.");
                    return Task.FromResult(false);
                }
                
                _logger.LogInformation("✓ Certificado tiene clave privada");
                
                // ✅ 5. Verificar hora del sistema (AFIP exige timestamp exacto)
                VerificarHoraDelSistema();
                
                // ⚠️ CRÍTICO: Verificar cruce de ambientes ANTES de continuar
                VerificarCruceDeAmbientes(cert);

                // Extraer CUIT del certificado
                string cuitDelCertificado = ExtraerCUITDelCertificado(cert);
                if (!string.IsNullOrEmpty(cuitDelCertificado))
                {
                    _logger.LogInformation("✓ Certificado válido: {Subject}", cert.Subject);
                    _logger.LogInformation("✓ CUIT extraído del certificado: {CuitCertificado}", cuitDelCertificado);
                    
                    // Validar que el CUIT del certificado coincida con el configurado
                    if (!string.IsNullOrWhiteSpace(_cuitServicio) && _cuitServicio != cuitDelCertificado)
                    {
                        _logger.LogError("⚠️ ADVERTENCIA CRÍTICA: El CUIT configurado en appsettings.json ({CuitConfigurado}) " +
                            "NO coincide con el CUIT del certificado ({CuitCertificado}). " +
                            "Esto causará errores de autenticación. Actualice 'CuitServicioAFIP' en appsettings.json.",
                            _cuitServicio, cuitDelCertificado);
                    }
                    else if (_cuitServicio == cuitDelCertificado)
                    {
                        _logger.LogInformation("✓ CUIT del certificado coincide con la configuración");
                    }
                }
                else
                {
                    _logger.LogWarning("⚠️ No se pudo extraer el CUIT del certificado. Verifique que el certificado sea válido.");
                }
                
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al verificar certificado: {Ruta}", rutaCertificado);
                return Task.FromResult(false);
            }
        }

        /// <summary>
        /// Extrae el CUIT del certificado desde el Subject
        /// </summary>
        private string ExtraerCUITDelCertificado(X509Certificate2 cert)
        {
            try
            {
                // El CUIT está en el Subject en formato: CN=..., SERIALNUMBER=CUIT CUIT_AQUI
                string subject = cert.Subject;
                _logger.LogDebug("Subject del certificado: {Subject}", subject);
                
                // Buscar el patrón SERIALNUMBER=CUIT XXXXXXXX
                var match = Regex.Match(subject, @"SERIALNUMBER\s*=\s*CUIT\s+(\d{11})", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
                
                // Intentar otro formato posible
                match = Regex.Match(subject, @"CUIT\s+(\d{11})", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
                
                return string.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al extraer CUIT del certificado");
                return string.Empty;
            }
        }

        /// <summary>
        /// Detecta si un certificado es de Homologación o Producción basándose en su DN (Distinguished Name)
        /// </summary>
        /// <param name="cert">Certificado a analizar</param>
        /// <returns>"Homologacion" si es de prueba, "Produccion" si es de producción, o "Desconocido" si no se puede determinar</returns>
        private string DetectarAmbienteDelCertificado(X509Certificate2 cert)
        {
            try
            {
                string subject = cert.Subject ?? string.Empty;
                string dnCompleto = subject.ToUpperInvariant();
                
                _logger.LogInformation("🔍 Analizando DN del certificado para detectar ambiente: {Subject}", subject);
                
                // Palabras clave que indican HOMOLOGACIÓN
                string[] palabrasHomologacion = {
                    "TEST", "TESTING", "HOMOLOGACION", "HOMOLOGACIÓN", 
                    "DEVELOPER", "DEVELOPMENT", "DEV", "WSAATEST", 
                    "WSEE/WSAA_TEST", "COMPUTADORES TEST", "PLAYA"
                };
                
                // Verificar si el DN contiene palabras de homologación
                foreach (string palabra in palabrasHomologacion)
                {
                    if (dnCompleto.Contains(palabra, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("⚠️ Certificado detectado como HOMOLOGACIÓN por palabra clave: '{Palabra}'", palabra);
                        return "Homologacion";
                    }
                }
                
                // Si no tiene palabras de homologación y tiene estructura típica de producción
                // (O=AFIP, OU=Servicio, CN=WSAA, etc.)
                if (dnCompleto.Contains("O=AFIP") && !dnCompleto.Contains("TEST"))
                {
                    _logger.LogInformation("✅ Certificado detectado como PRODUCCIÓN (estructura O=AFIP sin palabras de test)");
                    return "Produccion";
                }
                
                // Si tiene estructura clara de producción
                if (dnCompleto.Contains("CN=WSAA") && !dnCompleto.Contains("TEST"))
                {
                    _logger.LogInformation("✅ Certificado detectado como PRODUCCIÓN (CN=WSAA sin palabras de test)");
                    return "Produccion";
                }
                
                _logger.LogWarning("⚠️ No se pudo determinar el ambiente del certificado. DN: {Subject}", subject);
                return "Desconocido";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al detectar ambiente del certificado");
                return "Desconocido";
            }
        }

        /// <summary>
        /// Verifica la hora del sistema (AFIP exige timestamp exacto, desfase máximo 5 minutos)
        /// </summary>
        private void VerificarHoraDelSistema()
        {
            try
            {
                // Obtener hora del sistema local
                DateTime horaLocal = DateTime.Now;
                
                // Intentar obtener hora de un servidor NTP (opcional, pero útil)
                // Por ahora solo verificamos que la hora local sea razonable
                
                _logger.LogInformation("🕐 Verificando hora del sistema: {HoraLocal}", horaLocal);
                
                // Verificar que la hora no sea muy antigua o futura (indicaría problema de configuración)
                if (horaLocal.Year < 2020 || horaLocal.Year > 2030)
                {
                    _logger.LogError("❌ ERROR CRÍTICO: La hora del sistema parece incorrecta: {HoraLocal}\n\n" +
                        "AFIP exige timestamp exacto. Si la hora está desfasada más de 5 minutos, WSAA rechazará la solicitud.\n\n" +
                        "🔧 SOLUCIÓN:\n" +
                        "1. Sincronizá la hora de Windows con un servidor NTP\n" +
                        "2. Verificá la zona horaria (debe ser GMT-3 para Argentina)\n" +
                        "3. Reiniciá el sistema si es necesario", horaLocal);
                }
                else
                {
                    _logger.LogInformation("✓ Hora del sistema parece correcta: {HoraLocal}", horaLocal);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo verificar la hora del sistema (no crítico)");
            }
        }

        /// <summary>
        /// Verifica si hay un cruce de ambientes (certificado de homologación con URLs de producción o viceversa)
        /// </summary>
        private void VerificarCruceDeAmbientes(X509Certificate2 cert)
        {
            try
            {
                string ambienteCertificado = DetectarAmbienteDelCertificado(cert);
                string ambienteConfigurado = _entorno; // "Produccion" o "Homologacion"
                
                _logger.LogInformation("🔍 Verificando cruce de ambientes:");
                _logger.LogInformation("   - DN del certificado: {Subject}", cert.Subject);
                _logger.LogInformation("   - Ambiente del certificado: {AmbienteCert}", ambienteCertificado);
                _logger.LogInformation("   - Ambiente configurado: {AmbienteConfig}", ambienteConfigurado);
                _logger.LogInformation("   - URL WSAA: {UrlWSAA}", _urlWSAA);
                
                // Si no se pudo determinar el ambiente del certificado, no hacer nada
                if (ambienteCertificado == "Desconocido")
                {
                    _logger.LogWarning("⚠️ No se pudo determinar el ambiente del certificado. No se puede verificar cruce de ambientes.");
                    return;
                }
                
                // Verificar cruce de ambientes
                bool hayCruce = false;
                string mensajeError = "";
                
                if (ambienteCertificado == "Homologacion" && ambienteConfigurado == "Produccion")
                {
                    hayCruce = true;
                    mensajeError = $"❌ ERROR CRÍTICO: CRUCE DE AMBIENTES DETECTADO\n\n" +
                        $"El certificado es de HOMOLOGACIÓN pero estás configurado para PRODUCCIÓN.\n\n" +
                        $"📋 DETALLES:\n" +
                        $"   - Certificado: {cert.Subject}\n" +
                        $"   - Ambiente del certificado: HOMOLOGACIÓN\n" +
                        $"   - Ambiente configurado: PRODUCCIÓN\n" +
                        $"   - URL WSAA: {_urlWSAA}\n\n" +
                        $"🔧 SOLUCIÓN:\n" +
                        $"1. Si querés trabajar en HOMOLOGACIÓN:\n" +
                        $"   - Cambiá 'EsProduccion: false' en appsettings.json\n" +
                        $"   - Usá el certificado CertificadoHomologacion.pfx\n" +
                        $"   - Usá las URLs de homologación (wsaahomo.afip.gov.ar)\n\n" +
                        $"2. Si querés trabajar en PRODUCCIÓN:\n" +
                        $"   - Necesitás un certificado de PRODUCCIÓN (sin palabras 'test', 'testing', etc. en el DN)\n" +
                        $"   - El certificado debe estar generado en la sección de PRODUCCIÓN de AFIP\n" +
                        $"   - Mantené 'EsProduccion: true' en appsettings.json\n\n" +
                        $"⚠️ ARCA es extremadamente estricto: NO podés mezclar certificados de homologación con URLs de producción.";
                }
                else if (ambienteCertificado == "Produccion" && ambienteConfigurado == "Homologacion")
                {
                    hayCruce = true;
                    mensajeError = $"❌ ERROR CRÍTICO: CRUCE DE AMBIENTES DETECTADO\n\n" +
                        $"El certificado es de PRODUCCIÓN pero estás configurado para HOMOLOGACIÓN.\n\n" +
                        $"📋 DETALLES:\n" +
                        $"   - Certificado: {cert.Subject}\n" +
                        $"   - Ambiente del certificado: PRODUCCIÓN\n" +
                        $"   - Ambiente configurado: HOMOLOGACIÓN\n" +
                        $"   - URL WSAA: {_urlWSAA}\n\n" +
                        $"🔧 SOLUCIÓN:\n" +
                        $"1. Si querés trabajar en HOMOLOGACIÓN:\n" +
                        $"   - Usá un certificado de HOMOLOGACIÓN (generado en la sección Testing/Homologación de AFIP)\n" +
                        $"   - El certificado debe tener palabras como 'test', 'testing', 'homologacion' en el DN\n" +
                        $"   - Mantené 'EsProduccion: false' en appsettings.json\n\n" +
                        $"2. Si querés trabajar en PRODUCCIÓN:\n" +
                        $"   - Cambiá 'EsProduccion: true' en appsettings.json\n" +
                        $"   - Usá las URLs de producción (wsaa.afip.gov.ar)\n\n" +
                        $"⚠️ ARCA es extremadamente estricto: NO podés mezclar certificados de producción con URLs de homologación.";
                }
                
                if (hayCruce)
                {
                    _logger.LogError("⚠️ {Mensaje}", mensajeError);
                    throw new InvalidOperationException(mensajeError);
                }
                else
                {
                    _logger.LogInformation("✅ Verificación de cruce de ambientes: OK. Certificado y configuración coinciden.");
                }
            }
            catch (InvalidOperationException)
            {
                // Re-lanzar excepciones de cruce de ambientes
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al verificar cruce de ambientes (no crítico)");
            }
        }

        // ================================
        // CONSULTA PADRÓN (WS_SR_PADRON_A5)
        // ================================
        public async Task<PersonaAFIP> ConsultarPersonaAsync(string cuit)
        {
            try
            {
                // Limpiar CUIT: quitar guiones y espacios
                if (string.IsNullOrWhiteSpace(cuit))
                {
                    throw new ArgumentException("CUIT requerido");
                }
                
                cuit = cuit.Replace("-", "").Replace(" ", "").Trim();
                
                // Validar formato del CUIT (debe tener 11 dígitos)
                if (cuit.Length != 11 || !cuit.All(char.IsDigit))
                {
                    throw new ArgumentException($"CUIT inválido: {cuit}. Debe tener 11 dígitos numéricos.");
                }
                
                // Si está en modo demo, devolver datos simulados
                if (_modoDemo)
                {
                    _logger.LogInformation("Modo DEMO activado - devolviendo datos simulados para CUIT {Cuit}", cuit);
                    return new PersonaAFIP
                    {
                        Cuit = cuit,
                        RazonSocial = $"EMPRESA DEMO {cuit}",
                        Estado = "ACTIVO",
                        TipoPersona = "JURIDICA"
                    };
                }

                // Para consultas al padrón, usar el servicio ws_sr_padron_a5
                // 🔥 Obtener Token + Sign siempre válidos (WSAA) - Servicio: ws_sr_padron_a5
                var credenciales = await _wsaa.ObtenerCredencialesAsync("ws_sr_padron_a5");
                var xmlConsulta = CrearXMLConsultaWSP(cuit, credenciales.Token, credenciales.Sign);
                string respuesta = await EnviarConsultaWSPAsync(xmlConsulta);
                return ProcesarRespuestaWSP(respuesta, cuit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error consultando persona AFIP para CUIT {Cuit}", cuit);

                // En caso de error, si está en modo demo, devolver datos simulados
                if (_modoDemo)
                {
                    _logger.LogInformation("Error en modo demo - devolviendo datos simulados para CUIT {Cuit}", cuit);
                    return new PersonaAFIP
                    {
                        Cuit = cuit,
                        RazonSocial = $"EMPRESA DEMO {cuit}",
                        Estado = "ACTIVO",
                        TipoPersona = "JURIDICA"
                    };
                }

                // Retornar mensaje de error más descriptivo
                _logger.LogWarning("No se pudo consultar AFIP para CUIT {Cuit}. Verifique el certificado y la configuración.", cuit);
                return new PersonaAFIP 
                { 
                    Cuit = cuit, 
                    RazonSocial = $"Error al consultar AFIP. Verifique certificado y configuración." 
                };
            }
        }

        private string CrearXMLConsultaWSP(string cuit, string tokenString, string signString)
        {
            // ⚠️ CRÍTICO: El servicio WS_SR_PADRON_A5 espera una estructura específica
            // El orden de los elementos dentro de <a5:getPersona_v2> es OBLIGATORIO:
            // 1. token
            // 2. sign
            // 3. cuitRepresentada (DEBE ser el CUIT del certificado, NO el CUIT consultado)
            // 4. idPersona (el CUIT que se quiere consultar)
            // Todos deben ir dentro del nodo <a5:getPersona_v2> con el namespace correcto
            // El namespace del método debe ser xmlns:a5="http://a5.soap.ws.server.puc.sr/"
            
            // Validar que cuitRepresentada sea el CUIT del certificado
            if (string.IsNullOrWhiteSpace(_cuitServicio))
            {
                _logger.LogError("⚠️ CRÍTICO: CuitServicioAFIP no está configurado en appsettings.json");
                throw new InvalidOperationException("CuitServicioAFIP no está configurado. Verifique appsettings.json");
            }
            
            // Validar que el CUIT consultado sea diferente del CUIT representado
            if (_cuitServicio == cuit)
            {
                _logger.LogWarning("⚠️ ADVERTENCIA: El CUIT consultado ({CuitConsultado}) es el mismo que el CUIT representado ({CuitRepresentado}). " +
                    "Esto puede causar problemas si el certificado no tiene permisos para consultarse a sí mismo.", 
                    cuit, _cuitServicio);
            }
            
            _logger.LogInformation("📋 Creando XML para consulta WSP5:");
            _logger.LogInformation("   - cuitRepresentada (CUIT del certificado): {CuitRepresentado}", _cuitServicio);
            _logger.LogInformation("   - idPersona (CUIT a consultar): {CuitConsultado}", cuit);
            _logger.LogInformation("   - Entorno: {Entorno}", _entorno);
            
            string xml = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<soapenv:Envelope xmlns:soapenv=""http://schemas.xmlsoap.org/soap/envelope/""
                  xmlns:a5=""http://a5.soap.ws.server.puc.sr/"">
   <soapenv:Header/>
   <soapenv:Body>
      <a5:getPersona_v2>
         <token>{tokenString}</token>
         <sign>{signString}</sign>
         <idPersona>{cuit}</idPersona>
      </a5:getPersona_v2>
   </soapenv:Body>
</soapenv:Envelope>";
            
            _logger.LogDebug("XML generado (sin token/sign): <cuitRepresentada>{CuitRepresentado}</cuitRepresentada><idPersona>{CuitConsultado}</idPersona>", 
                _cuitServicio, cuit);
            
            return xml;
        }

        private async Task<string> EnviarConsultaWSPAsync(string xml)
        {
            // Asegurar TLS 1.2 para conexión con WSP5 (Padrón AFIP)
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            
            // Guardar el XML del request para debugging
            try
            {
                string logDir = GetTAStorageDir();
                File.WriteAllText(Path.Combine(logDir, "WSP5_Request.xml"), xml, Encoding.UTF8);
                _logger.LogDebug("XML Request WSP5 guardado en WSP5_Request.xml");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo guardar el XML del request");
            }
            
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeout) };
            var request = new HttpRequestMessage(HttpMethod.Post, _urlWSP5)
            {
                Content = new StringContent(xml, Encoding.UTF8, "text/xml")
            };

            request.Headers.Add("SOAPAction", "");
            request.Headers.Add("Accept", "text/xml");

            var response = await client.SendAsync(request);
            string content = await response.Content.ReadAsStringAsync();

            try
            {
                string logDir = GetTAStorageDir();
                File.WriteAllText(Path.Combine(logDir, "WSP5_Response.xml"), content, Encoding.UTF8);
            }
            catch { }

            return content;
        }

        private PersonaAFIP ProcesarRespuestaWSP(string xml, string cuit)
        {
            var persona = new PersonaAFIP { Cuit = cuit };

            // Log de la respuesta para debugging
            _logger.LogDebug("Procesando respuesta WSP5 para CUIT {Cuit}. XML (primeros 500 chars): {XmlPreview}", cuit, xml.Substring(0, Math.Min(500, xml.Length)));

            // Tolerar namespaces: <ns:razonSocial>, <ns:denominacion>, etc.
            // Buscar en diferentes formatos posibles de la respuesta de AFIP
            string razon = Regex.Match(xml, @"<(?:\w+:)?razonSocial>(.*?)</(?:\w+:)?razonSocial>", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(razon))
                razon = Regex.Match(xml, @"<(?:\w+:)?denominacion>(.*?)</(?:\w+:)?denominacion>", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(razon))
                razon = Regex.Match(xml, @"<(?:\w+:)?nombre>(.*?)</(?:\w+:)?nombre>", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(razon))
                razon = Regex.Match(xml, @"<(?:\w+:)?razonSocial[^>]*>(.*?)</(?:\w+:)?razonSocial>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value;

            // Si no se encuentra la razón social, verificar si hay un error en la respuesta
            if (string.IsNullOrEmpty(razon))
            {
                // Buscar errores en la respuesta - múltiples formatos posibles
                string error = Regex.Match(xml, @"<(?:\w+:)?faultstring>(.*?)</(?:\w+:)?faultstring>", RegexOptions.IgnoreCase).Groups[1].Value;
                
                // Si no se encuentra en faultstring, buscar en otros formatos
                if (string.IsNullOrEmpty(error))
                {
                    error = Regex.Match(xml, @"<(?:\w+:)?message>(.*?)</(?:\w+:)?message>", RegexOptions.IgnoreCase).Groups[1].Value;
                }
                if (string.IsNullOrEmpty(error))
                {
                    error = Regex.Match(xml, @"<(?:\w+:)?error>(.*?)</(?:\w+:)?error>", RegexOptions.IgnoreCase).Groups[1].Value;
                }
                
                if (!string.IsNullOrEmpty(error))
                {
                    _logger.LogWarning("Error en respuesta WSP5 para CUIT {Cuit}: {Error}. XML completo guardado en WSP5_Response.xml", cuit, error);
                    
                    // Mensaje más descriptivo para el error "No existe persona con ese Id"
                    if (error.Contains("No existe persona con ese Id", StringComparison.OrdinalIgnoreCase) ||
                        error.Contains("no existe persona", StringComparison.OrdinalIgnoreCase))
                    {
                        string mensajeDetallado;
                        
                        // Mensaje diferente según el entorno
                        if (_entorno == "Homologacion")
                        {
                            mensajeDetallado = $"⚠️ El CUIT {cuit} no está disponible en el padrón de Homologación.\n\n" +
                                $"📋 EXPLICACIÓN:\n" +
                                $"El entorno de Homologación tiene un padrón limitado con solo algunos CUITs de prueba.\n" +
                                $"Esto es NORMAL y NO afecta la facturación electrónica.\n\n" +
                                $"✅ PUEDES FACTURAR IGUAL:\n" +
                                $"La facturación electrónica (WSFEv1) funciona independientemente del padrón.\n" +
                                $"Puedes emitir facturas a cualquier CUIT o consumidor final sin consultar el padrón.\n\n" +
                                $"🧪 CUITs DE PRUEBA EN HOMOLOGACIÓN:\n" +
                                $"Si quieres probar la consulta, usa: 33693450239 (AFIP) o 20267565393 (prueba AFIP)\n\n" +
                                $"ℹ️ NOTA: En Producción, todos los CUITs válidos estarán disponibles.";
                            
                            _logger.LogWarning("⚠️ CUIT {CuitConsultado} no encontrado en padrón de Homologación. " +
                                "Esto es normal - Homologación tiene padrón limitado. La facturación no se ve afectada.",
                                cuit);
                        }
                        else
                        {
                            // Producción
                            mensajeDetallado = $"Error al consultar AFIP: {error}\n\n" +
                                $"POSIBLES CAUSAS:\n" +
                                $"1. El CUIT {cuit} no existe en el padrón de Producción\n" +
                                $"2. El certificado (CUIT {_cuitServicio}) no tiene autorizado el servicio WS_SR_PADRON_A5\n" +
                                $"3. El CUIT consultado no está disponible\n\n" +
                                $"SOLUCIÓN:\n" +
                                $"1. Verificar en el portal de AFIP que el servicio ws_sr_padron_a5 esté autorizado para el CUIT {_cuitServicio}\n" +
                                $"2. Verificar que el CUIT {cuit} sea válido y esté activo en AFIP";
                            
                            _logger.LogError("⚠️ Error 'No existe persona con ese Id' para CUIT {CuitConsultado} en Producción. " +
                                "CUIT representado (del certificado): {CuitRepresentado}",
                                cuit, _cuitServicio);
                        }
                        
                        persona.RazonSocial = mensajeDetallado;
                    }
                    else
                    {
                        persona.RazonSocial = $"Error al consultar AFIP: {error}";
                    }
                    
                    return persona;
                }

                // Si no hay error pero tampoco hay razón social, puede ser que el CUIT no esté en el padrón
                _logger.LogWarning("No se encontró razón social en respuesta WSP5 para CUIT {Cuit}. XML completo guardado en WSP5_Response.xml. Respuesta: {XmlPreview}", cuit, xml.Substring(0, Math.Min(500, xml.Length)));
                persona.RazonSocial = $"CUIT {cuit} no encontrado en padrón AFIP";
            }
            else
            {
                persona.RazonSocial = razon.Trim();
                _logger.LogInformation("✓ Razón social obtenida de AFIP para CUIT {Cuit}: {RazonSocial}", cuit, persona.RazonSocial);
            }

            var estado = Regex.Match(xml, @"<(?:\w+:)?estadoClave>(.*?)</(?:\w+:)?estadoClave>").Groups[1].Value;
            persona.Estado = string.IsNullOrEmpty(estado) ? "ACTIVO" : estado;

            var tipo = Regex.Match(xml, @"<(?:\w+:)?tipoPersona>(.*?)</(?:\w+:)?tipoPersona>").Groups[1].Value;
            persona.TipoPersona = string.IsNullOrEmpty(tipo) ? "JURIDICA" : tipo;

            // ⚠️ CRÍTICO: Obtener condición frente al IVA (obligatorio según RG 5616)
            // Buscar en diferentes formatos posibles de la respuesta de AFIP
            // El campo puede estar en diferentes ubicaciones según la versión del servicio
            string condicionIVAStr = Regex.Match(xml, @"<(?:\w+:)?condicionIVA>(.*?)</(?:\w+:)?condicionIVA>", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(condicionIVAStr))
                condicionIVAStr = Regex.Match(xml, @"<(?:\w+:)?condicionFrenteIVA>(.*?)</(?:\w+:)?condicionFrenteIVA>", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(condicionIVAStr))
                condicionIVAStr = Regex.Match(xml, @"<(?:\w+:)?impuestoGanancias>(.*?)</(?:\w+:)?impuestoGanancias>", RegexOptions.IgnoreCase).Groups[1].Value;
            if (string.IsNullOrEmpty(condicionIVAStr))
                condicionIVAStr = Regex.Match(xml, @"<(?:\w+:)?idImpuesto[^>]*>(\d+)</(?:\w+:)?idImpuesto>", RegexOptions.IgnoreCase).Groups[1].Value;
            
            // Buscar en datosRegimenGeneral si existe
            if (string.IsNullOrEmpty(condicionIVAStr))
            {
                var regimenMatch = Regex.Match(xml, @"<(?:\w+:)?datosRegimenGeneral[^>]*>.*?<(?:\w+:)?condicionIVA>(.*?)</(?:\w+:)?condicionIVA>.*?</(?:\w+:)?datosRegimenGeneral>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (regimenMatch.Success)
                    condicionIVAStr = regimenMatch.Groups[1].Value;
            }

            // Mapear condición frente al IVA según AFIP
            // Si no se encuentra, usar 4 (Consumidor Final) por defecto
            if (!string.IsNullOrEmpty(condicionIVAStr) && int.TryParse(condicionIVAStr.Trim(), out int condicionIVA))
            {
                persona.CondicionIVA = condicionIVA;
                _logger.LogInformation("✓ CondicionIVA obtenida de AFIP para CUIT {Cuit}: {CondicionIVA}", cuit, condicionIVA);
            }
            else
            {
                // Si no se puede obtener, usar valor por defecto: 4 = Consumidor Final
                persona.CondicionIVA = 4;
                _logger.LogWarning("No se pudo obtener CondicionIVA de AFIP para CUIT {Cuit}, usando valor por defecto: 4 (Consumidor Final)", cuit);
            }

            return persona;
        }

        // ================================
        // WSFE - FACTURACIÓN ELECTRÓNICA
        // ================================

        /// <summary>
        /// Obtiene el último número de comprobante autorizado para un punto de venta y tipo de comprobante
        /// </summary>
        public async Task<int> ObtenerUltimoNumeroComprobanteAsync(int puntoVenta, int tipoComprobante)
        {
            try
            {
                _logger.LogInformation("Obteniendo último número de comprobante - PuntoVenta: {PV}, Tipo: {Tipo}", puntoVenta, tipoComprobante);

                // 🔥 Obtener Token + Sign siempre válidos (WSAA) - Servicio: wsfe
                var credenciales = await _wsaa.ObtenerCredencialesAsync("wsfe");

                string xmlRequest = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" 
               xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" 
               xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <FECompUltimoAutorizado xmlns=""http://ar.gov.afip.dif.FEV1/"">
      <Auth>
        <Token>{credenciales.Token}</Token>`r`n        <Sign>{credenciales.Sign}</Sign>
        <Cuit>{_cuitServicio}</Cuit>
      </Auth>
      <PtoVta>{puntoVenta}</PtoVta>
      <CbteTipo>{tipoComprobante}</CbteTipo>
    </FECompUltimoAutorizado>
  </soap:Body>
</soap:Envelope>";

                // Asegurar TLS 1.2 para conexión con WSFE
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeout) };
                var request = new HttpRequestMessage(HttpMethod.Post, _urlWSFE)
                {
                    Content = new StringContent(xmlRequest, Encoding.UTF8, "text/xml")
                };

                request.Headers.Add("SOAPAction", "\"http://ar.gov.afip.dif.FEV1/FECompUltimoAutorizado\"");

                var response = await client.SendAsync(request);
                string responseXml = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError("Error HTTP {StatusCode} al obtener último comprobante: {Response}", response.StatusCode, responseXml);
                    throw new HttpRequestException($"Error HTTP {response.StatusCode}: {responseXml}");
                }

                // Guardar respuesta para debug
                try
                {
                    string logDir = GetTAStorageDir();
                    File.WriteAllText(Path.Combine(logDir, "FECompUltimoAutorizado_Response.xml"), responseXml, Encoding.UTF8);
                }
                catch { }

                // Extraer número de comprobante
                var match = Regex.Match(responseXml, @"<CbteNro>(.*?)</CbteNro>");
                if (match.Success && int.TryParse(match.Groups[1].Value, out int numero))
                {
                    _logger.LogInformation("Último número de comprobante: {Numero}", numero);
                    return numero;
                }

                _logger.LogWarning("No se pudo extraer el número de comprobante de la respuesta");
                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al obtener último número de comprobante");
                throw;
            }
        }

        /// <summary>
        /// Autoriza un comprobante electrónico y obtiene el CAE (método original con XML manual)
        /// </summary>
        public async Task<(bool autorizado, string? cae, DateTime? fechaVtoCAE, string? numeroComprobante, string? observaciones)>
    AutorizarComprobanteAsync(Models.Comprobante comprobante)
        {
            try
            {
                _logger.LogInformation("Autorizando comprobante - PuntoVenta: {PV}, Tipo: {Tipo}",
                    comprobante.PuntoVenta, comprobante.TipoComprobante);

                // ==== MODO DEMO ====
                if (_modoDemo)
                {
                    string caeSimulado =
                        $"DEMO{DateTime.Now:yyyyMMdd}{comprobante.PuntoVenta:D3}{comprobante.TipoComprobante:D2}{new Random().Next(100, 999)}";

                    return (true, caeSimulado, DateTime.Now.AddDays(10), "1",
                        "Comprobante autorizado en modo DEMO (simulado)");
                }

                // 🔥 Obtener Token + Sign siempre válidos (WSAA) - Servicio: wsfe
                var credenciales = await _wsaa.ObtenerCredencialesAsync("wsfe");

                // ==== NUEVO NUMERO DE COMPROBANTE ====
                int ultimoNumero = await ObtenerUltimoNumeroComprobanteAsync(comprobante.PuntoVenta,
                    comprobante.TipoComprobante);
                int nuevoNumero = ultimoNumero + 1;

                // ==== REDONDEO ====
                comprobante.ImporteTotal = Math.Round(comprobante.ImporteTotal, 2);
                comprobante.ImporteNeto = Math.Round(comprobante.ImporteNeto, 2);
                comprobante.ImporteIVA = Math.Round(comprobante.ImporteIVA, 2);

                // ==== ALICUOTAS IVA ====
                string alicuotasIVA = "";

                var alicuotasAgrupadas = comprobante.Detalles
                    .GroupBy(d => d.AlicuotaIVA)
                    .Select(g => new
                    {
                        Alicuota = g.Key,
                        BaseImp = Math.Round(g.Sum(d => d.Cantidad * d.PrecioUnitario), 2),
                        Importe = Math.Round(g.Sum(d => (d.Cantidad * d.PrecioUnitario) * (d.AlicuotaIVA / 100)), 2)
                    })
                    .ToList();

                foreach (var alic in alicuotasAgrupadas)
                {
                    int idAlicuota = alic.Alicuota switch
                    {
                        0 => 3,
                        10.5m => 4,
                        21m => 5,
                        27m => 6,
                        _ => 5
                    };

                    alicuotasIVA += $@"
            <AlicIva>
                <Id>{idAlicuota}</Id>
                <BaseImp>{alic.BaseImp.ToString("F2", CultureInfo.InvariantCulture)}</BaseImp>
                <Importe>{alic.Importe.ToString("F2", CultureInfo.InvariantCulture)}</Importe>
            </AlicIva>";
                }

                // ==== TIPO DE DOC ====
                int docTipo = (!string.IsNullOrEmpty(comprobante.CuitCliente) &&
                               comprobante.CuitCliente.Replace("-", "").Length == 11)
                    ? 80
                    : 99;

                // ==== DOC NUM ====
                long docNro = docTipo == 80
                    ? long.Parse(comprobante.CuitCliente!.Replace("-", ""))
                    : 0;

                // ==== CONDICIÓN IVA RECEPTOR (RG 5616) ====
                int valor = comprobante.CondicionIVAReceptor ?? 5;

                // ==== DETALLE XML ====
                string xmlDetalle = $@"
<FECAEDetRequest>
    <Concepto>{comprobante.Concepto}</Concepto>
    <DocTipo>{docTipo}</DocTipo>
    <DocNro>{docNro}</DocNro>

    <CbteDesde>{nuevoNumero}</CbteDesde>
    <CbteHasta>{nuevoNumero}</CbteHasta>
    <CbteFch>{DateTime.Now:yyyyMMdd}</CbteFch>

    <ImpTotal>{comprobante.ImporteTotal.ToString("F2", CultureInfo.InvariantCulture)}</ImpTotal>
    <ImpTotConc>0.00</ImpTotConc>
    <ImpNeto>{comprobante.ImporteNeto.ToString("F2", CultureInfo.InvariantCulture)}</ImpNeto>
    <ImpIVA>{comprobante.ImporteIVA.ToString("F2", CultureInfo.InvariantCulture)}</ImpIVA>
    <ImpTrib>0.00</ImpTrib>

    <MonId>PES</MonId>
    <MonCotiz>1.00</MonCotiz>

    <CondicionIVAReceptor>{valor}</CondicionIVAReceptor>

    <Iva>
        {alicuotasIVA}
    </Iva>

    <Opcionales>
      <Opcional>
        <Id>2101</Id>
        <Valor>{valor}</Valor>
      </Opcional>
    </Opcionales>
</FECAEDetRequest>";


                // ==== XML FINAL ====
                string xmlRequest = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
  <soap:Body>
    <FECAESolicitar xmlns=""http://ar.gov.afip.dif.FEV1/"">
      <Auth>
        <Token>{credenciales.Token}</Token>`r`n        <Sign>{credenciales.Sign}</Sign>
        <Cuit>{_cuitServicio}</Cuit>
      </Auth>
      <FeCAEReq>
        <FeCabReq>
          <CantReg>1</CantReg>
          <PtoVta>{comprobante.PuntoVenta}</PtoVta>
          <CbteTipo>{comprobante.TipoComprobante}</CbteTipo>
        </FeCabReq>
        <FeDetReq>
          {xmlDetalle}
        </FeDetReq>
      </FeCAEReq>
    </FECAESolicitar>
  </soap:Body>
</soap:Envelope>";

                // ==== GUARDAR XML ====
                try
                {
                    File.WriteAllText(Path.Combine(GetTAStorageDir(),
                            $"FECAESolicitar_Request_{DateTime.Now:yyyyMMddHHmmss}.xml"),
                        xmlRequest, Encoding.UTF8);
                }
                catch
                {
                }

                // ==== ENVIAR ====
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeout) };

                var request = new HttpRequestMessage(HttpMethod.Post, _urlWSFE)
                {
                    Content = new StringContent(xmlRequest, Encoding.UTF8, "text/xml")
                };
                request.Headers.Add("SOAPAction", "\"http://ar.gov.afip.dif.FEV1/FECAESolicitar\"");

                var response = await client.SendAsync(request);
                string responseXml = await response.Content.ReadAsStringAsync();

                try
                {
                    File.WriteAllText(Path.Combine(GetTAStorageDir(),
                            $"FECAESolicitar_Response_{DateTime.Now:yyyyMMddHHmmss}.xml"),
                        responseXml, Encoding.UTF8);
                }
                catch
                {
                }

                if (!response.IsSuccessStatusCode)
                    return (false, null, null, null, $"Error HTTP {response.StatusCode}");

                return ProcesarRespuestaFECAESolicitar(responseXml, nuevoNumero);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al autorizar comprobante");
                return (false, null, null, null, ex.Message);
            }
        }



        private (bool autorizado, string? cae, DateTime? fechaVtoCAE, string? numeroComprobante, string? observaciones) ProcesarRespuestaFECAESolicitar(string responseXml, int numeroComprobante)
        {
            try
            {
                // Extraer CAE
                var matchCAE = Regex.Match(responseXml, @"<CAE>(.*?)</CAE>");
                string? cae = matchCAE.Success ? matchCAE.Groups[1].Value : null;

                // Extraer fecha de vencimiento CAE
                var matchVto = Regex.Match(responseXml, @"<CAEFchVto>(.*?)</CAEFchVto>");
                DateTime? fechaVto = null;
                if (matchVto.Success)
                {
                    string fechaStr = matchVto.Groups[1].Value;
                    if (DateTime.TryParseExact(fechaStr, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime fecha))
                    {
                        fechaVto = fecha;
                    }
                }

                // Extraer observaciones/errores
                var matchObs = Regex.Match(responseXml, @"<Observaciones>.*?<Obs>(.*?)</Obs>.*?</Observaciones>", RegexOptions.Singleline);
                string? observaciones = matchObs.Success ? matchObs.Groups[1].Value : null;

                // Verificar si fue autorizado
                var matchResultado = Regex.Match(responseXml, @"<Resultado>(.*?)</Resultado>");
                bool autorizado = matchResultado.Success && matchResultado.Groups[1].Value == "A";

                if (autorizado && !string.IsNullOrEmpty(cae))
                {
                    _logger.LogInformation("Comprobante autorizado - CAE: {CAE}, Vto: {Vto}", cae, fechaVto);
                    return (true, cae, fechaVto, numeroComprobante.ToString("D8"), observaciones);
                }
                else
                {
                    _logger.LogWarning("Comprobante rechazado - Observaciones: {Obs}", observaciones);
                    return (false, null, null, null, observaciones ?? "Comprobante rechazado por AFIP");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al procesar respuesta FECAESolicitar");
                return (false, null, null, null, $"Error procesando respuesta: {ex.Message}");
            }
        }


        /// <summary>
        /// Autoriza un comprobante usando el método que retorna un objeto compatible con el controlador
        /// </summary>
        public async Task<FECAEResponse?> AutorizarComprobanteConWSFEServiceAsync(Models.Comprobante comprobante)
        {
            try
            {
                // Si está en modo demo, devolver respuesta simulada
                if (_modoDemo)
                {
                    _logger.LogInformation("Modo DEMO activado - devolviendo autorización simulada para comprobante {Tipo}-{PV}", 
                        comprobante.TipoComprobante, comprobante.PuntoVenta);
                    
                    // Generar CAE simulado (máximo 20 caracteres para compatibilidad)
                    // Formato: DEMO + fecha(8) + PV(3) + tipo(2) + random(3) = 20 caracteres
                    string caeSimulado = $"DEMO{DateTime.Now:yyyyMMdd}{comprobante.PuntoVenta:D3}{comprobante.TipoComprobante:D2}{new Random().Next(100, 999)}";
                    
                    return new FECAEResponse
                    {
                        FeDetResp = new[]
                        {
                            new FECAEDetResponse
                            {
                                CAE = caeSimulado,
                                CAEFchVto = DateTime.Now.AddDays(10).ToString("yyyyMMdd"),
                                CbteDesde = 1,
                                CbteHasta = 1,
                                Resultado = "A",
                                Observaciones = "Comprobante autorizado en modo DEMO (simulado)"
                            }
                        }
                    };
                }

                var resultado = await AutorizarComprobanteAsync(comprobante);
                
                if (resultado.autorizado)
                {
                    return new FECAEResponse
                    {
                        FeDetResp = new[]
                        {
                            new FECAEDetResponse
                            {
                                CAE = resultado.cae ?? "",
                                CAEFchVto = resultado.fechaVtoCAE?.ToString("yyyyMMdd") ?? "",
                                CbteDesde = int.Parse(resultado.numeroComprobante ?? "0"),
                                CbteHasta = int.Parse(resultado.numeroComprobante ?? "0"),
                                Resultado = "A",
                                Observaciones = resultado.observaciones
                            }
                        }
                    };
                }
                else
                {
                    return new FECAEResponse
                    {
                        FeDetResp = new[]
                        {
                            new FECAEDetResponse
                            {
                                Resultado = "R",
                                Observaciones = resultado.observaciones ?? "Comprobante rechazado"
                            }
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en AutorizarComprobanteConWSFEServiceAsync");
                return null;
            }
        }

        /// <summary>
        /// Emite una factura electrónica a través de AFIP WSFE
        /// Método simplificado que retorna un ResultadoFactura
        /// </summary>
        /// <param name="comprobante">Comprobante a autorizar</param>
        /// <returns>Resultado de la emisión con CAE, fecha de vencimiento y número de comprobante</returns>
        public async Task<ResultadoFactura> EmitirFacturaAsync(Models.Comprobante comprobante)
        {
            try
            {
                _logger.LogInformation("Emitiendo factura - PuntoVenta: {PV}, Tipo: {Tipo}", comprobante.PuntoVenta, comprobante.TipoComprobante);

                // Usar el método existente de autorización
                var resultado = await AutorizarComprobanteAsync(comprobante);

                if (resultado.autorizado && !string.IsNullOrEmpty(resultado.cae))
                {
                    return new ResultadoFactura
                    {
                        CAE = resultado.cae,
                        FechaVencimiento = resultado.fechaVtoCAE ?? DateTime.Now.AddDays(10),
                        NumeroComprobante = long.Parse(resultado.numeroComprobante ?? "0"),
                        Autorizado = true,
                        Observaciones = resultado.observaciones
                    };
                }
                else
                {
                    return new ResultadoFactura
                    {
                        CAE = string.Empty,
                        FechaVencimiento = DateTime.Now,
                        NumeroComprobante = 0,
                        Autorizado = false,
                        Observaciones = resultado.observaciones ?? "Comprobante rechazado por AFIP"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al emitir factura");
                return new ResultadoFactura
                {
                    CAE = string.Empty,
                    FechaVencimiento = DateTime.Now,
                    NumeroComprobante = 0,
                    Autorizado = false,
                    Observaciones = $"Error al emitir factura: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Obtiene el CUIT del servicio configurado
        /// </summary>
        public string GetCuitServicio()
        {
            return _cuitServicio;
        }

        // ================================
        // VERIFICACIÓN DE CONEXIONES AFIP
        // ================================

        /// <summary>
        /// Verifica la conectividad con los servicios de AFIP
        /// </summary>
        public async Task<Dictionary<string, object>> VerificarConexionesAFIPAsync()
        {
            var resultados = new Dictionary<string, object>();
            
            try
            {
                // 1. Verificar certificado
                bool certificadoOk = false;
                string mensajeCertificado = "";
                if (!string.IsNullOrEmpty(_certificadoPath))
                {
                    certificadoOk = await VerificarCertificadoAsync(_certificadoPath, _certPassword);
                    mensajeCertificado = certificadoOk 
                        ? "Certificado válido y no expirado" 
                        : "Certificado inválido, no encontrado o expirado";
                }
                else
                {
                    mensajeCertificado = "Certificado no configurado";
                }

                resultados["Certificado"] = new
                {
                    Ok = certificadoOk,
                    Mensaje = mensajeCertificado,
                    Ruta = _certificadoPath ?? "No configurado"
                };

                // 2. Verificar conectividad con WSAA
                bool wsaaOk = false;
                string mensajeWSAA = "";
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                    // Usar el timeout configurado en appsettings.json (default 30 segundos) en lugar de 10
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeout) };
                    var response = await client.GetAsync(_urlWSAA);
                    wsaaOk = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
                    mensajeWSAA = wsaaOk 
                        ? $"WSAA accesible (Status: {response.StatusCode})" 
                        : $"WSAA no accesible (Status: {response.StatusCode})";
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    mensajeWSAA = $"WSAA: Timeout al conectar (el servicio puede estar lento o inaccesible). Timeout configurado: {_timeout}s";
                    _logger.LogWarning("Timeout al verificar WSAA. Esto puede ser normal si el servicio está lento.");
                }
                catch (Exception ex)
                {
                    mensajeWSAA = $"Error al conectar con WSAA: {ex.Message}";
                }

                resultados["WSAA"] = new
                {
                    Ok = wsaaOk,
                    Mensaje = mensajeWSAA,
                    Url = _urlWSAA
                };

                // 3. Verificar conectividad con WSFE
                bool wsfeOk = false;
                string mensajeWSFE = "";
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                    // Usar el timeout configurado en appsettings.json (default 30 segundos) en lugar de 10
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeout) };
                    var response = await client.GetAsync(_urlWSFE);
                    wsfeOk = response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
                    mensajeWSFE = wsfeOk 
                        ? $"WSFE accesible (Status: {response.StatusCode})" 
                        : $"WSFE no accesible (Status: {response.StatusCode})";
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    mensajeWSFE = $"WSFE: Timeout al conectar (el servicio puede estar lento o inaccesible). Timeout configurado: {_timeout}s";
                    _logger.LogWarning("Timeout al verificar WSFE. Esto puede ser normal si el servicio está lento.");
                }
                catch (Exception ex)
                {
                    mensajeWSFE = $"Error al conectar con WSFE: {ex.Message}";
                }

                resultados["WSFE"] = new
                {
                    Ok = wsfeOk,
                    Mensaje = mensajeWSFE,
                    Url = _urlWSFE
                };

                // 4. Verificar conectividad con WSP5
                bool wsp5Ok = false;
                string mensajeWSP5 = "";
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
                    // Usar el timeout configurado en appsettings.json (default 30 segundos) en lugar de 10
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(_timeout) };
                    var response = await client.GetAsync(_urlWSP5);
                    // WSP5 puede devolver InternalServerError (500) en algunos casos y aún ser accesible
                    // También aceptamos MethodNotAllowed (405) que indica que el endpoint existe
                    wsp5Ok = response.IsSuccessStatusCode || 
                             response.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed ||
                             response.StatusCode == System.Net.HttpStatusCode.InternalServerError;
                    
                    if (response.StatusCode == System.Net.HttpStatusCode.InternalServerError)
                    {
                        mensajeWSP5 = $"WSP5 accesible (Status: {response.StatusCode} - puede ser normal, el servicio requiere autenticación)";
                        wsp5Ok = true; // Consideramos accesible si responde, aunque sea con error
                    }
                    else
                    {
                        mensajeWSP5 = wsp5Ok 
                            ? $"WSP5 accesible (Status: {response.StatusCode})" 
                            : $"WSP5 no accesible (Status: {response.StatusCode})";
                    }
                }
                catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
                {
                    mensajeWSP5 = $"WSP5: Timeout al conectar (el servicio puede estar lento o inaccesible). Timeout configurado: {_timeout}s";
                    _logger.LogWarning("Timeout al verificar WSP5. Esto puede ser normal si el servicio está lento.");
                }
                catch (Exception ex)
                {
                    mensajeWSP5 = $"Error al conectar con WSP5: {ex.Message}";
                }

                resultados["WSP5"] = new
                {
                    Ok = wsp5Ok,
                    Mensaje = mensajeWSP5,
                    Url = _urlWSP5
                };

                // 5. Intentar obtener token (si certificado está OK)
                bool tokenOk = false;
                string mensajeToken = "";
                if (certificadoOk && !_modoDemo)
                {
                    try
                    {
                        var token = await ObtenerTokenAsync();
                        tokenOk = token != null && !string.IsNullOrEmpty(token.Token);
                        mensajeToken = tokenOk 
                            ? $"Token obtenido exitosamente (válido hasta {token.ExpirationTime:dd/MM/yyyy HH:mm})" 
                            : "No se pudo obtener el token";
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        // Error de autorización - mostrar mensaje detallado
                        mensajeToken = $"❌ ERROR DE AUTORIZACIÓN: {ex.Message}";
                        tokenOk = false;
                    }
                    catch (Exception ex)
                    {
                        // Verificar si es un error de autorización en el mensaje
                        if (ex.Message.Contains("no autorizado", StringComparison.OrdinalIgnoreCase) ||
                            ex.Message.Contains("Computador no autorizado", StringComparison.OrdinalIgnoreCase))
                        {
                            mensajeToken = $"❌ ERROR DE AUTORIZACIÓN AFIP:\n\n" +
                                $"El certificado (CUIT {_cuitServicio}) NO está asociado al servicio.\n\n" +
                                $"El certificado no está asociado al Web Service en el Administrador de Relaciones de AFIP.\n\n" +
                                $"Si no ves los servicios nacionales en el listado, contactá a ARCA o AFIP.\n\n" +
                                $"Error: {ex.Message}";
                        }
                        else
                        {
                            mensajeToken = $"Error al obtener token: {ex.Message}";
                        }
                        tokenOk = false;
                    }
                }
                else if (_modoDemo)
                {
                    mensajeToken = "Modo demo activo - no se requiere token real";
                    tokenOk = true;
                }
                else
                {
                    mensajeToken = "Certificado no válido - no se puede obtener token";
                }

                resultados["Token"] = new
                {
                    Ok = tokenOk,
                    Mensaje = mensajeToken
                };

                // 6. Probar consulta al padrón (solo si certificado y token están OK)
                // 🚫 IMPORTANTE: RECE NO utiliza padrón. AFIP no aprobará el servicio si se habilita.
                // "Consumidor Final" se completa manual o con RG5616
                bool consultaPadronOk = false;
                string mensajeConsultaPadron = "";
                if (_consultarPadron && certificadoOk && tokenOk && !_modoDemo)
                {
                    try
                    {
                        // Intentar consultar el propio CUIT del certificado primero (debería funcionar si el servicio está autorizado)
                        _logger.LogInformation("Probando consulta al padrón con CUIT del certificado: {Cuit}", _cuitServicio);
                        var persona = await ConsultarPersonaAsync(_cuitServicio);
                        
                        if (!string.IsNullOrEmpty(persona.RazonSocial) && 
                            !persona.RazonSocial.Contains("Error") && 
                            !persona.RazonSocial.Contains("no encontrado") &&
                            !persona.RazonSocial.Contains("No existe persona"))
                        {
                            consultaPadronOk = true;
                            mensajeConsultaPadron = $"✓ Consulta exitosa. Razón Social: {persona.RazonSocial}";
                        }
                        else
                        {
                            // Si falla con el propio CUIT, es muy probable que el servicio no esté autorizado
                            if (persona.RazonSocial.Contains("No existe persona"))
                            {
                                mensajeConsultaPadron = $"✗ CRÍTICO: Error al consultar propio CUIT ({_cuitServicio}): {persona.RazonSocial}\n\n" +
                                    $"⚠️ DIAGNÓSTICO: El servicio 'ws_sr_padron_a5' probablemente NO está autorizado.\n\n" +
                                    $"SOLUCIÓN:\n" +
                                    $"1. Ir al portal de AFIP: https://www.afip.gob.ar/fe/\n" +
                                    $"2. Entrar a 'Administrador de Relaciones'\n" +
                                    $"3. Buscar el alias 'appplaya2025' (o tu alias)\n" +
                                    $"4. Autorizar el servicio 'ws_sr_padron_a5'\n" +
                                    $"5. Esperar 15 minutos y volver a probar\n\n" +
                                    $"Ver guía completa en: Documentacion/Guia_Autorizar_Servicio_AFIP.md";
                            }
                            else
                            {
                                mensajeConsultaPadron = $"✗ Error: {persona.RazonSocial}";
                            }
                            _logger.LogWarning("Consulta al padrón falló: {Mensaje}", persona.RazonSocial);
                        }
                    }
                    catch (Exception ex)
                    {
                        mensajeConsultaPadron = $"✗ Error al consultar padrón: {ex.Message}";
                        _logger.LogError(ex, "Error al probar consulta al padrón");
                    }
                }
                else if (!_consultarPadron)
                {
                    mensajeConsultaPadron = "🚫 Consulta al padrón deshabilitada (RECE no utiliza padrón)";
                }
                else if (_modoDemo)
                {
                    mensajeConsultaPadron = "Modo Demo activado - prueba omitida";
                    consultaPadronOk = true;
                }
                else
                {
                    mensajeConsultaPadron = "Certificado o token no válido - no se puede probar consulta";
                }

                resultados["PruebaPadron"] = new
                {
                    Ok = consultaPadronOk,
                    Mensaje = mensajeConsultaPadron,
                    CuitProbado = _cuitServicio,
                    Entorno = _entorno
                };

                // Resumen general
                bool todoOk = certificadoOk && wsaaOk && wsfeOk && wsp5Ok && tokenOk && consultaPadronOk;
                resultados["Resumen"] = new
                {
                    TodoOk = todoOk,
                    Entorno = _entorno,
                    ModoDemo = _modoDemo,
                    CuitServicio = _cuitServicio
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al verificar conexiones AFIP");
                resultados["Error"] = new
                {
                    Ok = false,
                    Mensaje = $"Error general: {ex.Message}"
                };
            }

            return resultados;
        }

        /// <summary>
        /// Verifica si el entorno actual está listo para usar (no demo)
        /// </summary>
        public async Task<bool> VerificarEntornoListoAsync()
        {
            if (_modoDemo)
                return false;

            var verificaciones = await VerificarConexionesAFIPAsync();
            
            if (verificaciones.ContainsKey("Resumen"))
            {
                var resumen = verificaciones["Resumen"];
                // Usar reflexión para obtener la propiedad TodoOk
                var tipo = resumen.GetType();
                var prop = tipo.GetProperty("TodoOk");
                if (prop != null)
                {
                    return (bool)(prop.GetValue(resumen) ?? false);
                }
            }

            return false;
        }

        // Métodos auxiliares
        public Task<bool> ValidarCUITAsync(string cuit)
        {
            bool valido = cuit.All(char.IsDigit) && cuit.Length == 11;
            _logger.LogInformation("ValidarCUITAsync: {Cuit} => {Valido}", cuit, valido);
            return Task.FromResult(valido);
        }

        /// <summary>
        /// Limpia archivos de respuesta WSAA viejos (más de 24 horas) para evitar acumulación
        /// </summary>
        private void LimpiarRespuestasViejas()
        {
            try
            {
                // Limpiar solo una vez por hora para no impactar el rendimiento
                if ((DateTime.Now - _lastCleanup).TotalHours < 1)
                {
                    return;
                }

                _lastCleanup = DateTime.Now;
                string certDir = GetTAStorageDir();

                if (!Directory.Exists(certDir))
                {
                    return;
                }

                var archivosViejos = Directory.GetFiles(certDir, "WSAA_Response_*.xml")
                    .Where(f =>
                    {
                        try
                        {
                            var info = new FileInfo(f);
                            // Eliminar archivos más viejos de 24 horas
                            return (DateTime.Now - info.LastWriteTime).TotalHours > 24;
                        }
                        catch
                        {
                            return false;
                        }
                    })
                    .ToList();

                int eliminados = 0;
                foreach (var archivo in archivosViejos)
                {
                    try
                    {
                        File.Delete(archivo);
                        eliminados++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "No se pudo eliminar archivo viejo: {Archivo}", archivo);
                    }
                }

                if (eliminados > 0)
                {
                    _logger.LogInformation("✓ Limpieza automática: {Count} archivos de respuesta WSAA eliminados (más de 24 horas)", eliminados);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error al limpiar archivos de respuesta viejos");
            }
        }

        /// <summary>
        /// Limpia los tokens almacenados en disco para forzar la obtención de un nuevo token
        /// </summary>
        public async Task<bool> LimpiarTokensAsync()
        {
            try
            {
                // Limpiar también el caché en memoria (todos los servicios)
                await _tokenSemaphore.WaitAsync();
                try
                {
                    // Limpiar tokens de todos los servicios
                    var keysToRemove = _tokenCache.Keys.Where(k => k.StartsWith($"{_cuitServicio}_{_entorno}_")).ToList();
                    foreach (var key in keysToRemove)
                    {
                        _tokenCache.Remove(key);
                        _logger.LogInformation("Token removido del caché en memoria: {Key}", key);
                    }
                    if (keysToRemove.Count > 0)
                    {
                        _logger.LogInformation("✓ {Count} token(s) removido(s) del caché en memoria", keysToRemove.Count);
                    }
                }
                finally
                {
                    _tokenSemaphore.Release();
                }

                string certDir = GetTAStorageDir();
                _logger.LogInformation("Limpiando tokens en directorio: {Dir}", certDir);
                _logger.LogInformation("Ruta completa: {FullPath}", Path.GetFullPath(certDir));

                if (!Directory.Exists(certDir))
                {
                    _logger.LogWarning("El directorio de tokens no existe: {Dir}", certDir);
                    return false;
                }

                // Buscar todos los archivos relacionados con tokens
                var archivosToken = new List<string>();
                
                // Buscar archivos TA específicos por servicio (TA_wsfe.xml, TA_ws_sr_padron_a5.xml, etc.)
                try
                {
                    var archivosTAServicio = Directory.GetFiles(certDir, "TA_*.xml");
                    foreach (var archivo in archivosTAServicio)
                    {
                        // Excluir archivos WSAA_Response
                        if (!Path.GetFileName(archivo).StartsWith("WSAA_Response_", StringComparison.OrdinalIgnoreCase))
                        {
                            archivosToken.Add(archivo);
                        }
                    }
                    _logger.LogInformation("Encontrados {Count} archivos TA específicos por servicio", archivosToken.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error al buscar archivos TA por servicio");
                }
                
                // También buscar archivos antiguos (sin servicio específico) para compatibilidad
                string cleanPath = Path.Combine(certDir, "TA_clean.xml");
                string taPath = Path.Combine(certDir, "TA.xml");
                
                if (File.Exists(cleanPath) && !archivosToken.Contains(cleanPath)) archivosToken.Add(cleanPath);
                if (File.Exists(taPath) && !archivosToken.Contains(taPath)) archivosToken.Add(taPath);

                // Buscar también archivos de respuesta WSAA que puedan contener tokens
                // Estos archivos pueden ser útiles para debugging, pero si hay muchos, pueden causar confusión
                try
                {
                    var archivosWSAA = Directory.GetFiles(certDir, "WSAA_Response_*.xml");
                    _logger.LogInformation("Encontrados {Count} archivos de respuesta WSAA", archivosWSAA.Length);
                    
                    // Eliminar archivos WSAA_Response que contengan errores "alreadyAuthenticated" 
                    // (estos no tienen tokens válidos y solo ocupan espacio)
                    int eliminadosWSAA = 0;
                    foreach (var archivoWSAA in archivosWSAA)
                    {
                        try
                        {
                            string contenido = File.ReadAllText(archivoWSAA, Encoding.UTF8);
                            if (contenido.Contains("alreadyAuthenticated", StringComparison.OrdinalIgnoreCase) ||
                                contenido.Contains("soapenv:Fault", StringComparison.OrdinalIgnoreCase) ||
                                contenido.Contains("soap:Fault", StringComparison.OrdinalIgnoreCase))
                            {
                                File.Delete(archivoWSAA);
                                eliminadosWSAA++;
                                _logger.LogInformation("✓ Archivo WSAA con error eliminado: {Archivo}", Path.GetFileName(archivoWSAA));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error al procesar archivo WSAA {Archivo}", Path.GetFileName(archivoWSAA));
                        }
                    }
                    
                    if (eliminadosWSAA > 0)
                    {
                        _logger.LogInformation("✓ {Count} archivo(s) WSAA_Response con errores eliminado(s)", eliminadosWSAA);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error al buscar archivos WSAA");
                }

                bool eliminado = false;
                foreach (var archivo in archivosToken)
                {
                    try
                    {
                        if (File.Exists(archivo))
                        {
                            File.Delete(archivo);
                            _logger.LogInformation("✓ Token eliminado: {Archivo}", Path.GetFileName(archivo));
                            eliminado = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error al eliminar archivo {Archivo}", archivo);
                    }
                }

                if (eliminado)
                {
                    _logger.LogInformation("✓ Tokens limpiados exitosamente. Se obtendrá un nuevo token en la próxima solicitud.");
                }
                else
                {
                    _logger.LogInformation("No se encontraron tokens para limpiar en {Dir}", certDir);
                }

                return eliminado;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al limpiar tokens: {Mensaje}", ex.Message);
                return false;
            }
        }

        // Método auxiliar para decodificar HTML (compatible con .NET 8)
        private static string HtmlDecode(string html)
        {
            return html
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&")
                .Replace("&quot;", "\"")
                .Replace("&apos;", "'");
        }
    }
}

