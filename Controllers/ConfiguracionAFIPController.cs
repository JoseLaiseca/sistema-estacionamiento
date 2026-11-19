using EstacionamientoWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace EstacionamientoWebApp.Controllers
{
    [Authorize]
    public class ConfiguracionAFIPController : Controller
    {
        private readonly AFIPService _afipService;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _env;
        private readonly ILogger<ConfiguracionAFIPController> _logger;

        public ConfiguracionAFIPController(
            AFIPService afipService,
            IConfiguration configuration,
            IWebHostEnvironment env,
            ILogger<ConfiguracionAFIPController> logger)
        {
            _afipService = afipService;
            _configuration = configuration;
            _env = env;
            _logger = logger;
        }

        // GET: /ConfiguracionAFIP
        public async Task<IActionResult> Index()
        {
            var verificaciones = await _afipService.VerificarConexionesAFIPAsync();
            var modoDemo = _configuration.GetValue<bool>("AFIP:ModoDemo", false);
            var esProduccion = _configuration.GetValue<bool>("AFIP:EsProduccion", false);
            var entorno = esProduccion ? "Produccion" : "Homologacion";

            ViewBag.Verificaciones = verificaciones;
            ViewBag.ModoDemo = modoDemo;
            ViewBag.EsProduccion = esProduccion;
            ViewBag.Entorno = entorno;

            return View();
        }

        // POST: /ConfiguracionAFIP/Verificar
        [HttpPost]
        public async Task<IActionResult> Verificar()
        {
            try
            {
                var verificaciones = await _afipService.VerificarConexionesAFIPAsync();
                return Json(new { success = true, verificaciones });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al verificar conexiones AFIP");
                return Json(new { success = false, mensaje = $"Error: {ex.Message}" });
            }
        }

        // POST: /ConfiguracionAFIP/LimpiarTokens
        [HttpPost]
        public async Task<IActionResult> LimpiarTokens()
        {
            try
            {
                var resultado = await _afipService.LimpiarTokensAsync();
                if (resultado)
                {
                    return Json(new { success = true, mensaje = "Tokens limpiados exitosamente. Se obtendrá un nuevo token en la próxima solicitud." });
                }
                else
                {
                    return Json(new { success = false, mensaje = "No se encontraron tokens para limpiar o hubo un error al eliminarlos." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al limpiar tokens");
                return Json(new { success = false, mensaje = $"Error: {ex.Message}" });
            }
        }

        // POST: /ConfiguracionAFIP/CambiarEntorno
        [HttpPost]
        public async Task<IActionResult> CambiarEntorno([FromBody] CambiarEntornoRequest request)
        {
            try
            {
                if (request == null || string.IsNullOrEmpty(request.NuevoEntorno))
                {
                    return Json(new { success = false, mensaje = "Entorno no especificado" });
                }

                var nuevoEntorno = request.NuevoEntorno;
                var modoDemoActual = _configuration.GetValue<bool>("AFIP:ModoDemo", false);
                var esProduccionActual = _configuration.GetValue<bool>("AFIP:EsProduccion", false);

                // Validar transición
                if (nuevoEntorno == "Homologacion" && modoDemoActual)
                {
                    // Verificar que todo esté listo antes de pasar de demo a homologación
                    var verificaciones = await _afipService.VerificarConexionesAFIPAsync();
                    if (verificaciones.ContainsKey("Resumen"))
                    {
                        var resumen = verificaciones["Resumen"];
                        var tipo = resumen.GetType();
                        var prop = tipo.GetProperty("TodoOk");
                        if (prop != null && !(bool)(prop.GetValue(resumen) ?? false))
                        {
                            return Json(new 
                            { 
                                success = false, 
                                mensaje = "No se puede cambiar a Homologación. Verifique que el certificado esté configurado y que las conexiones con AFIP funcionen correctamente.",
                                verificaciones 
                            });
                        }
                    }
                }
                else if (nuevoEntorno == "Produccion" && !esProduccionActual)
                {
                    // Verificar que todo esté listo antes de pasar a producción
                    var verificaciones = await _afipService.VerificarConexionesAFIPAsync();
                    if (verificaciones.ContainsKey("Resumen"))
                    {
                        var resumen = verificaciones["Resumen"];
                        var tipo = resumen.GetType();
                        var prop = tipo.GetProperty("TodoOk");
                        if (prop != null && !(bool)(prop.GetValue(resumen) ?? false))
                        {
                            return Json(new 
                            { 
                                success = false, 
                                mensaje = "No se puede cambiar a Producción. Verifique que el certificado esté configurado y que las conexiones con AFIP funcionen correctamente.",
                                verificaciones 
                            });
                        }
                    }
                }

                // Actualizar appsettings.json
                var appsettingsPath = Path.Combine(_env.ContentRootPath, "appsettings.json");
                var json = await System.IO.File.ReadAllTextAsync(appsettingsPath);
                var jsonDoc = JsonDocument.Parse(json);
                var root = jsonDoc.RootElement;

                // Crear nuevo JSON con los cambios
                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                writer.WriteStartObject();

                // Copiar todas las secciones excepto AFIP
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name != "AFIP")
                    {
                        prop.WriteTo(writer);
                    }
                }

                // Escribir nueva sección AFIP
                writer.WriteStartObject("AFIP");
                writer.WriteBoolean("EsProduccion", nuevoEntorno == "Produccion");
                writer.WriteBoolean("ModoDemo", nuevoEntorno == "Demo");

                // Copiar configuración de Homologacion
                writer.WriteStartObject("Homologacion");
                var homologacion = root.GetProperty("AFIP").GetProperty("Homologacion");
                foreach (var prop in homologacion.EnumerateObject())
                {
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();

                // Copiar configuración de Produccion
                writer.WriteStartObject("Produccion");
                var produccion = root.GetProperty("AFIP").GetProperty("Produccion");
                foreach (var prop in produccion.EnumerateObject())
                {
                    prop.WriteTo(writer);
                }
                writer.WriteEndObject();

                writer.WriteEndObject(); // AFIP
                writer.WriteEndObject(); // Root

                writer.Flush();
                var nuevoJson = System.Text.Encoding.UTF8.GetString(stream.ToArray());

                // Guardar archivo
                await System.IO.File.WriteAllTextAsync(appsettingsPath, nuevoJson);

                _logger.LogInformation("Configuración AFIP actualizada: {Entorno}", nuevoEntorno);

                return Json(new 
                { 
                    success = true, 
                    mensaje = $"Configuración actualizada a {nuevoEntorno}. Reinicie la aplicación para aplicar los cambios.",
                    nuevoEntorno 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al cambiar entorno AFIP");
                return Json(new { success = false, mensaje = $"Error: {ex.Message}" });
            }
        }
    }

    public class CambiarEntornoRequest
    {
        public string NuevoEntorno { get; set; } = string.Empty;
    }
}

