using EstacionamientoWebApp.Models;
using EstacionamientoWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QuestPDF;

namespace EstacionamientoWebApp.Controllers
{
    [Authorize]
    public class FacturacionController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly AFIPService _afipService;
        private readonly ILogger<FacturacionController> _logger;
        private readonly IConfiguration _configuration;

        public FacturacionController(
            ApplicationDbContext context,
            AFIPService afipService,
            ILogger<FacturacionController> logger,
            IConfiguration configuration)
        {
            _context = context;
            _afipService = afipService;
            _logger = logger;
            _configuration = configuration;
        }

        // GET: Facturacion/Crear
        public async Task<IActionResult> Crear(int? ingresoId = null, int? estadiaId = null)
        {
            // Obtener punto de venta desde configuración (7 para Producción según appsettings.json)
            int puntoVenta = _configuration.GetValue<int>("AFIP:Produccion:PuntoVenta", 1);
            bool esProduccion = _configuration.GetValue<bool>("AFIP:EsProduccion", false);
            
            // Si no está en producción, usar 1 por defecto (Homologación)
            if (!esProduccion)
            {
                puntoVenta = _configuration.GetValue<int>("AFIP:Homologacion:PuntoVenta", 1);
            }
            
            var comprobante = new Comprobante
            {
                PuntoVenta = puntoVenta, // Usar punto de venta desde configuración (7 en Producción)
                TipoComprobante = 6, // Factura B por defecto
                FechaEmision = DateTime.Now,
                Concepto = "1",
                CondicionVenta = "1",
                TipoDoc = 99, // Consumidor Final por defecto
                NumeroDoc = "0",
                CondicionIVAReceptor = 5 // Consumidor Final
            };

            // Si viene un ingresoId, precargar datos del ingreso
            if (ingresoId.HasValue)
            {
                var ingreso = await _context.Ingresos
                    .FirstOrDefaultAsync(i => i.Id == ingresoId.Value);

                if (ingreso != null)
                {
                    // Precargar datos del ingreso
                    comprobante.RazonSocialCliente = "Consumidor Final"; // Por defecto
                    comprobante.Detalles.Add(new DetalleComprobante
                    {
                        Descripcion = $"Estacionamiento - {ingreso.TipoVehiculo} - Patente: {ingreso.Patente}",
                        Cantidad = 1,
                        PrecioUnitario = 0, // El usuario debe completar el monto
                        AlicuotaIVA = 21
                    });
                }
            }
            // Si viene un estadiaId, precargar datos de la estadía
            else if (estadiaId.HasValue)
            {
                var estadia = await _context.Estadias
                    .FirstOrDefaultAsync(e => e.Id == estadiaId.Value);

                if (estadia != null)
                {
                    // Precargar datos de la estadía
                    comprobante.RazonSocialCliente = estadia.NombreCliente;
                    comprobante.Detalles.Add(new DetalleComprobante
                    {
                        Descripcion = $"Estadía Prolongada - {estadia.TipoVehiculo} - Patente: {estadia.Patente} - {estadia.DiasEstadia} días",
                        Cantidad = 1,
                        PrecioUnitario = estadia.Monto,
                        AlicuotaIVA = 21
                    });
                }
            }
            else
            {
                // Agregar un detalle vacío para que el usuario pueda completar
                comprobante.Detalles.Add(new DetalleComprobante
                {
                    Descripcion = "",
                    Cantidad = 1,
                    PrecioUnitario = 0,
                    AlicuotaIVA = 21
                });
            }

            return View(comprobante);
        }

        // POST: Facturacion/Crear
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Crear(Comprobante comprobante)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    return View(comprobante);
                }

                // ⚠️ CRÍTICO: Asegurar que se use el punto de venta correcto desde configuración
                bool esProduccion = _configuration.GetValue<bool>("AFIP:EsProduccion", false);
                int puntoVentaConfigurado = esProduccion 
                    ? _configuration.GetValue<int>("AFIP:Produccion:PuntoVenta", 1)
                    : _configuration.GetValue<int>("AFIP:Homologacion:PuntoVenta", 1);
                
                // Si el punto de venta no coincide con el configurado, corregirlo
                if (comprobante.PuntoVenta != puntoVentaConfigurado)
                {
                    _logger.LogWarning("⚠️ Punto de venta del comprobante ({PVComprobante}) no coincide con el configurado ({PVConfigurado}). " +
                        "Corrigiendo automáticamente.", comprobante.PuntoVenta, puntoVentaConfigurado);
                    comprobante.PuntoVenta = puntoVentaConfigurado;
                }
                
                _logger.LogInformation("✅ Usando Punto de Venta {PV} para comprobante (entorno: {Entorno})", 
                    comprobante.PuntoVenta, esProduccion ? "Producción" : "Homologación");

                // Validar que tenga al menos un detalle
                if (comprobante.Detalles == null || comprobante.Detalles.Count == 0)
                {
                    ModelState.AddModelError("", "Debe agregar al menos un detalle al comprobante.");
                    return View(comprobante);
                }

                // Si no es CUIT, AFIP ignora CuitCliente
                if (comprobante.TipoDoc != 80)
                {
                    comprobante.CuitCliente = null;
                }

                // ⚠️ CRÍTICO: Log de valores recibidos del formulario (JS)
                _logger.LogInformation(
                    "📥 VALORES RECIBIDOS DEL FORMULARIO (JS) -> ImporteNeto: {Neto}, ImporteIVA: {IVA}, ImporteTotal: {Total}",
                    comprobante.ImporteNeto,
                    comprobante.ImporteIVA,
                    comprobante.ImporteTotal
                );

                // ⚠️ CRÍTICO: NO recalcular si ya vienen valores del formulario
                // El JS ya calculó correctamente los totales
                // Solo recalcular si los valores están en 0 (caso de carga inicial sin datos)
                if (comprobante.ImporteTotal == 0 && comprobante.ImporteNeto == 0 && comprobante.ImporteIVA == 0)
                {
                    _logger.LogWarning("⚠️ Valores en 0, recalculando desde detalles (esto no debería pasar si el JS funciona)");
                    CalcularTotales(comprobante);
                }
                else
                {
                    _logger.LogInformation("✅ Usando valores del formulario (JS) - NO se recalculan");
                }

                // ⚠️ CRÍTICO: Log de valores ANTES de enviar a AFIP
                _logger.LogInformation(
                    "📤 VALORES ANTES DE ENVIAR A AFIP -> ImporteNeto: {Neto}, ImporteIVA: {IVA}, ImporteTotal: {Total}",
                    comprobante.ImporteNeto,
                    comprobante.ImporteIVA,
                    comprobante.ImporteTotal
                );

                // ⚠️ CRÍTICO: Asegurar que CondicionIVA tenga un valor (obligatorio según RG 5616)
                // Si no se especificó, usar 4 (Consumidor Final) por defecto
                if (!comprobante.CondicionIVA.HasValue || comprobante.CondicionIVA < 1 || comprobante.CondicionIVA > 11)
                {
                    comprobante.CondicionIVA = 4; // Consumidor Final
                    _logger.LogWarning("CondicionIVA no especificada o inválida, usando valor por defecto: 4 (Consumidor Final)");
                }

                // Guardar comprobante en BD (pendiente de autorización)
                comprobante.Estado = "Pendiente";
                comprobante.FechaCreacion = DateTime.Now;

                // Asegurar que los detalles estén correctamente configurados
                // Entity Framework asignará automáticamente el ComprobanteId al guardar
                foreach (var detalle in comprobante.Detalles)
                {
                    // Asegurar que el detalle tenga valores válidos
                    if (string.IsNullOrEmpty(detalle.Descripcion))
                    {
                        detalle.Descripcion = "Sin descripción";
                    }
                    if (detalle.Cantidad <= 0)
                    {
                        detalle.Cantidad = 1;
                    }
                    // El ComprobanteId se asignará automáticamente cuando se guarde el comprobante
                }

                _context.Comprobantes.Add(comprobante);
                
                try
                {
                    await _context.SaveChangesAsync();
                    _logger.LogInformation("Comprobante guardado exitosamente con ID: {Id}", comprobante.Id);
                }
                catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
                {
                    // Capturar el inner exception para más detalles
                    string errorDetallado = dbEx.InnerException?.Message ?? dbEx.Message;
                    string innerExceptionCompleto = dbEx.InnerException?.ToString() ?? "Sin inner exception";
                    
                    _logger.LogError(dbEx, "Error al guardar comprobante en BD: {Error}", errorDetallado);
                    _logger.LogError("Inner Exception completo: {InnerException}", innerExceptionCompleto);
                    _logger.LogError("Stack Trace: {StackTrace}", dbEx.StackTrace);
                    
                    // Log de los datos del comprobante para debugging
                    _logger.LogError("Datos del comprobante - PuntoVenta: {PV}, Tipo: {Tipo}, Numero: {Numero}, CAE: {CAE}, Estado: {Estado}", 
                        comprobante.PuntoVenta, comprobante.TipoComprobante, comprobante.NumeroComprobante, comprobante.CAE, comprobante.Estado);
                    
                    throw new Exception($"Error al guardar en base de datos: {errorDetallado}. Detalles: {innerExceptionCompleto}", dbEx);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error inesperado al guardar comprobante");
                    throw new Exception($"Error inesperado al guardar en base de datos: {ex.Message}", ex);
                }

                // ⚠️ RG 5616 - Forzar Condición IVA del Receptor
                // Si es Consumidor Final (TipoDoc 99), o Factura C (TipoComprobante 11),
                // o no hay CUIT válido, AFIP exige que CondicionIVAReceptor sea 5 (Consumidor Final).
                if (comprobante.TipoDoc == 99 
                    || comprobante.TipoComprobante == 11 
                    || string.IsNullOrWhiteSpace(comprobante.CuitCliente))
                {
                    comprobante.CondicionIVAReceptor = 5;
                }

                // Log de debug para ver qué estamos mandando realmente
                _logger.LogInformation(
                    "DEBUG RG5616 -> TipoDoc: {TipoDoc}, TipoCbte: {TipoCbte}, CondIVAReceptor: {CondIVAReceptor}",
                    comprobante.TipoDoc,
                    comprobante.TipoComprobante,
                    comprobante.CondicionIVAReceptor
                );

                // Intentar autorizar con AFIP usando WSFEService
                var fecaResponse = await _afipService.AutorizarComprobanteConWSFEServiceAsync(comprobante);

                if (fecaResponse?.FeDetResp != null && fecaResponse.FeDetResp.Length > 0)
                {
                    var detResp = fecaResponse.FeDetResp[0];
                    
                    if (detResp.Resultado == "A" && !string.IsNullOrEmpty(detResp.CAE))
                    {
                        // Actualizar comprobante con datos de AFIP
                        comprobante.CAE = detResp.CAE;
                        
                        if (!string.IsNullOrEmpty(detResp.CAEFchVto) && 
                            DateTime.TryParseExact(detResp.CAEFchVto, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out DateTime fechaVto))
                        {
                            comprobante.FechaVtoCAE = fechaVto;
                        }
                        
                        comprobante.NumeroComprobante = detResp.CbteDesde.ToString();
                        comprobante.Estado = "Autorizado";
                        comprobante.FechaActualizacion = DateTime.Now;

                        if (!string.IsNullOrEmpty(detResp.Observaciones))
                        {
                            comprobante.Observaciones = detResp.Observaciones;
                        }

                        try
                        {
                            await _context.SaveChangesAsync();
                            _logger.LogInformation("Comprobante actualizado exitosamente con CAE: {CAE}", detResp.CAE);
                        }
                        catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
                        {
                            string errorDetallado = dbEx.InnerException?.Message ?? dbEx.Message;
                            _logger.LogError(dbEx, "Error al actualizar comprobante con CAE: {Error}", errorDetallado);
                            _logger.LogError("Inner Exception: {InnerException}", dbEx.InnerException?.ToString() ?? "Sin inner exception");
                            
                            // Aunque falle el guardado, el comprobante ya está en BD con el ID
                            // Intentar redirigir de todas formas
                            TempData["Warning"] = $"Comprobante autorizado por AFIP (CAE: {detResp.CAE}), pero hubo un error al actualizar en BD: {errorDetallado}";
                            return RedirectToAction(nameof(Ver), new { id = comprobante.Id });
                        }

                        TempData["Success"] = $"Comprobante autorizado exitosamente. CAE: {detResp.CAE}";
                        return RedirectToAction(nameof(Ver), new { id = comprobante.Id });
                    }
                    else
                    {
                        // Comprobante rechazado
                        comprobante.Estado = "Rechazado";
                        comprobante.Observaciones = detResp.Observaciones ?? $"Resultado: {detResp.Resultado}";
                        comprobante.FechaActualizacion = DateTime.Now;

                        await _context.SaveChangesAsync();

                        TempData["Error"] = $"Comprobante rechazado por AFIP: {comprobante.Observaciones}";
                        return View(comprobante);
                    }
                }
                else
                {
                    // Error al obtener respuesta - pero guardamos el comprobante para poder imprimirlo
                    comprobante.Estado = "Error";
                    comprobante.Observaciones = "No se pudo obtener respuesta de AFIP. El comprobante fue guardado pero no autorizado.";
                    comprobante.FechaActualizacion = DateTime.Now;

                    await _context.SaveChangesAsync();

                    TempData["Warning"] = "El comprobante fue guardado pero no pudo ser autorizado por AFIP. Puede verlo e imprimirlo desde el listado.";
                    return RedirectToAction(nameof(Ver), new { id = comprobante.Id });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al crear comprobante");
                
                // Obtener el mensaje de error más detallado
                string mensajeError = ex.Message;
                if (ex.InnerException != null)
                {
                    mensajeError += $" | Detalle: {ex.InnerException.Message}";
                }
                
                // Mensaje de error más descriptivo según el tipo
                if (ex.Message.Contains("Certificado"))
                {
                    mensajeError = "Error de certificado: Verifique que el archivo CertificadoHomologacion.pfx existe en la carpeta Certificados y que la contraseña sea correcta. Para trabajar en modo demo, active 'ModoDemo: true' en appsettings.json";
                }
                else if (ex.Message.Contains("saving the entity changes") || ex.Message.Contains("base de datos"))
                {
                    mensajeError = $"Error al guardar en base de datos: {ex.InnerException?.Message ?? ex.Message}. Verifique que las migraciones estén aplicadas y que la base de datos esté accesible.";
                }
                else
                {
                    mensajeError = $"Error al procesar comprobante: {mensajeError}";
                }
                
                TempData["Error"] = mensajeError;
                
                // Si el comprobante no se guardó, intentar recargar los detalles para la vista
                if (comprobante.Id == 0 && comprobante.Detalles != null && comprobante.Detalles.Count > 0)
                {
                    // Asegurar que los detalles estén disponibles para la vista
                    foreach (var detalle in comprobante.Detalles)
                    {
                        detalle.Comprobante = comprobante;
                    }
                }
                
                return View(comprobante);
            }
        }

        // GET: Facturacion/Listar
        public async Task<IActionResult> Listar()
        {
            var comprobantes = await _context.Comprobantes
                .Include(c => c.Detalles)
                .OrderByDescending(c => c.FechaCreacion)
                .ToListAsync();

            return View(comprobantes);
        }

        // GET: Facturacion/Ver/5
        public async Task<IActionResult> Ver(int id)
        {
            var comprobante = await _context.Comprobantes
                .Include(c => c.Detalles)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (comprobante == null)
            {
                return NotFound();
            }

            return View(comprobante);
        }

        // GET: Facturacion/GenerarQR/{id}
        [HttpGet]
        public IActionResult GenerarQR(int id)
        {
            try
            {
                var comprobante = _context.Comprobantes
                    .FirstOrDefault(c => c.Id == id);

                if (comprobante == null || string.IsNullOrEmpty(comprobante.CAE))
                {
                    // Retornar imagen vacía si no hay CAE
                    return File(new byte[0], "image/png");
                }

                // Obtener CUIT del servicio desde configuración
                var cuitServicio = _afipService.GetCuitServicio();
                
                // Construir JSON para el QR según formato AFIP
                // Formato: {"ver":1,"fecha":"YYYY-MM-DD","cuit":CUIT,"ptoVta":PV,"tipoCmp":TIPO,"nroCmp":NUMERO,"importe":IMPORTE,"moneda":"PES","ctz":1,"tipoDocRec":TIPO_DOC,"nroDocRec":NRO_DOC,"tipoCodAut":"E","codAut":CAE}
                var tipoDocRec = string.IsNullOrEmpty(comprobante.CuitCliente) ? 99 : 80; // 99=Consumidor Final, 80=CUIT
                var nroDocRec = string.IsNullOrEmpty(comprobante.CuitCliente) ? 0 : long.Parse(comprobante.CuitCliente.Replace("-", ""));

                var datosQR = $"{{\"ver\":1,\"fecha\":\"{comprobante.FechaEmision:yyyy-MM-dd}\"," +
                             $"\"cuit\":{cuitServicio}," +
                             $"\"ptoVta\":{comprobante.PuntoVenta}," +
                             $"\"tipoCmp\":{comprobante.TipoComprobante}," +
                             $"\"nroCmp\":{comprobante.NumeroComprobante}," +
                             $"\"importe\":{comprobante.ImporteTotal:F2}," +
                             $"\"moneda\":\"PES\",\"ctz\":1," +
                             $"\"tipoDocRec\":{tipoDocRec},\"nroDocRec\":{nroDocRec}," +
                             $"\"tipoCodAut\":\"E\",\"codAut\":{comprobante.CAE}}}";

                // Generar QR usando QRCoder
                using var qrGenerator = new global::QRCoder.QRCodeGenerator();
                var qrData = qrGenerator.CreateQrCode(datosQR, global::QRCoder.QRCodeGenerator.ECCLevel.Q);
                using var qrCode = new global::QRCoder.PngByteQRCode(qrData);
                var qrBytes = qrCode.GetGraphic(20);

                // Retornar imagen PNG
                return File(qrBytes, "image/png");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al generar QR para comprobante {Id}", id);
                // Retornar imagen vacía en caso de error
                return File(new byte[0], "image/png");
            }
        }

        // POST: Facturacion/ImprimirDirecto/{id}
        [HttpPost]
        [IgnoreAntiforgeryToken] // Permitir llamadas AJAX sin token
        [SupportedOSPlatform("windows")]
        public async Task<IActionResult> ImprimirDirecto(int id, string? nombreImpresora = null)
        {
            try
            {
                var comprobante = await _context.Comprobantes
                    .Include(c => c.Detalles)
                    .FirstOrDefaultAsync(c => c.Id == id);

                if (comprobante == null)
                    return Json(new { success = false, mensaje = "Comprobante no encontrado" });

                // Obtener nombre de impresora desde configuración o parámetro
                nombreImpresora ??= _configuration["Impresora:Nombre"] ?? "POS-80C"; // Impresora por defecto

                // Obtener tipo de comprobante como texto
                string tipoComprobanteTexto = comprobante.TipoComprobante switch
                {
                    1 => "Factura A",
                    6 => "Factura B",
                    11 => "Factura C",
                    _ => $"Tipo {comprobante.TipoComprobante}"
                };

                // Construir texto del ticket
                var texto = new System.Text.StringBuilder();
                texto.AppendLine("    ESTACIONAMIENTO DEMO");
                texto.AppendLine("        CUIT 20-00000000-0");
                texto.AppendLine("  Av. Ejemplo 123 - Ciudad");
                texto.AppendLine("-----------------------------------");
                texto.AppendLine();
                texto.AppendLine($"{tipoComprobanteTexto} N° {comprobante.PuntoVenta:D4}-{comprobante.NumeroComprobante ?? "Pendiente"}");
                texto.AppendLine($"Fecha: {comprobante.FechaEmision:dd/MM/yyyy HH:mm}");
                texto.AppendLine($"Cliente: {comprobante.RazonSocialCliente ?? "Consumidor Final"}");
                texto.AppendLine($"CUIT: {comprobante.CuitCliente ?? "-"}");
                texto.AppendLine("-----------------------------------");
                texto.AppendLine();

                // Detalles
                foreach (var det in comprobante.Detalles)
                {
                    texto.AppendLine(det.Descripcion ?? "Sin descripción");
                    texto.AppendLine($"Cant: {det.Cantidad:N2}   P.Unit: ${det.PrecioUnitario:N2}");
                    texto.AppendLine($"IVA: {det.AlicuotaIVA:N2}%   Subtotal: ${det.Subtotal:N2}");
                    texto.AppendLine("-----------------------------------");
                }

                texto.AppendLine();
                texto.AppendLine($"Subtotal Neto: ${comprobante.ImporteNeto:N2}");
                texto.AppendLine($"IVA: ${comprobante.ImporteIVA:N2}");
                texto.AppendLine($"TOTAL: ${comprobante.ImporteTotal:N2}");
                texto.AppendLine("===================================");

                // Datos AFIP
                if (!string.IsNullOrEmpty(comprobante.CAE))
                {
                    texto.AppendLine();
                    texto.AppendLine($"CAE: {comprobante.CAE}");
                    if (comprobante.FechaVtoCAE.HasValue)
                    {
                        texto.AppendLine($"Vto CAE: {comprobante.FechaVtoCAE.Value:dd/MM/yyyy}");
                    }
                    texto.AppendLine($"Estado: {comprobante.Estado}");
                }

                texto.AppendLine("===================================");
                if (comprobante.Estado == "Autorizado" && !string.IsNullOrEmpty(comprobante.Observaciones) && comprobante.Observaciones.Contains("DEMO"))
                {
                    texto.AppendLine("Comprobante autorizado en modo DEMO");
                }
                else
                {
                    texto.AppendLine("Comprobante autorizado por AFIP");
                }
                texto.AppendLine(DateTime.Now.ToString("dd/MM/yyyy HH:mm"));
                texto.AppendLine();
                texto.AppendLine("    ¡Gracias por su visita!");

                string textoTicket = texto.ToString();

                // Imprimir directamente usando PrintDocument
                using (PrintDocument pd = new PrintDocument())
                {
                    pd.PrinterSettings.PrinterName = nombreImpresora;

                    pd.PrintPage += (sender, e) =>
                    {
                        if (e.Graphics == null) return;
                        float y = 0;

                        // Texto principal
                        using (Font fuenteNormal = new Font("Consolas", 10))
                        {
                            e.Graphics.DrawString(textoTicket, fuenteNormal, Brushes.Black, 0, y);
                            y += e.Graphics.MeasureString(textoTicket, fuenteNormal).Height;
                        }

                        // Generar e imprimir QR si existe CAE
                        if (!string.IsNullOrEmpty(comprobante.CAE))
                        {
                            try
                            {
                                var cuitServicio = _afipService.GetCuitServicio();
                                var tipoDocRec = string.IsNullOrEmpty(comprobante.CuitCliente) ? 99 : 80;
                                var nroDocRec = string.IsNullOrEmpty(comprobante.CuitCliente) ? 0 : long.Parse(comprobante.CuitCliente.Replace("-", ""));

                                var datosQR = $"{{\"ver\":1,\"fecha\":\"{comprobante.FechaEmision:yyyy-MM-dd}\"," +
                                             $"\"cuit\":{cuitServicio}," +
                                             $"\"ptoVta\":{comprobante.PuntoVenta}," +
                                             $"\"tipoCmp\":{comprobante.TipoComprobante}," +
                                             $"\"nroCmp\":{comprobante.NumeroComprobante}," +
                                             $"\"importe\":{comprobante.ImporteTotal:F2}," +
                                             $"\"moneda\":\"PES\",\"ctz\":1," +
                                             $"\"tipoDocRec\":{tipoDocRec},\"nroDocRec\":{nroDocRec}," +
                                             $"\"tipoCodAut\":\"E\",\"codAut\":{comprobante.CAE}}}";

                                using var qrGenerator = new global::QRCoder.QRCodeGenerator();
                                var qrData = qrGenerator.CreateQrCode(datosQR, global::QRCoder.QRCodeGenerator.ECCLevel.Q);
                                using var qrCode = new global::QRCoder.PngByteQRCode(qrData);
                                
                                // Generar QR más pequeño para impresora térmica (5 píxeles por módulo)
                                var qrBytes = qrCode.GetGraphic(5);
                                
                                // Convertir bytes a Bitmap para imprimir
                                using (var ms = new System.IO.MemoryStream(qrBytes))
                                {
                                    using var qrBitmap = new Bitmap(ms);
                                    
                                    // Calcular tamaño máximo para impresora térmica de 80mm (aprox 300 píxeles a 96 DPI)
                                    int maxWidth = 250; // Ancho máximo para 80mm
                                    int maxHeight = 250; // Alto máximo
                                    
                                    // Redimensionar si es necesario manteniendo la proporción
                                    int newWidth = qrBitmap.Width;
                                    int newHeight = qrBitmap.Height;
                                    
                                    if (qrBitmap.Width > maxWidth || qrBitmap.Height > maxHeight)
                                    {
                                        double scale = Math.Min((double)maxWidth / qrBitmap.Width, (double)maxHeight / qrBitmap.Height);
                                        newWidth = (int)(qrBitmap.Width * scale);
                                        newHeight = (int)(qrBitmap.Height * scale);
                                    }
                                    
                                    // Centrar el QR en el ancho de la impresora
                                    int xPos = (maxWidth - newWidth) / 2;
                                    
                                    e.Graphics.DrawImage(qrBitmap, new Rectangle(xPos, (int)y, newWidth, newHeight));
                                    y += newHeight + 10;
                                }

                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "No se pudo generar QR para impresión");
                            }
                        }

                        // Texto pequeño al final
                        using (Font fuenteChica = new Font("Consolas", 7))
                        {
                            string textoPequeno = "\n\n\n";
                            e.Graphics.DrawString(textoPequeno, fuenteChica, Brushes.Black, 0, y);
                        }
                    };

                    pd.Print();
                }

                return Json(new { success = true, mensaje = "Comprobante enviado a imprimir correctamente" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al imprimir comprobante {Id}", id);
                return Json(new { success = false, mensaje = $"Error al imprimir: {ex.Message}" });
            }
        }

        // GET: Facturacion/Imprimir/{id} - Genera PDF
        [HttpGet]
        public async Task<IActionResult> Imprimir(int id)
        {
            try
            {
                // Configurar licencia de QuestPDF (Community - gratuita)
                Settings.License = LicenseType.Community;

                var comprobante = await _context.Comprobantes
                    .Include(c => c.Detalles)
                    .FirstOrDefaultAsync(c => c.Id == id);

                if (comprobante == null)
                    return NotFound();

                // Obtener tipo de comprobante como texto
                string tipoComprobanteTexto = comprobante.TipoComprobante switch
                {
                    1 => "Factura A",
                    6 => "Factura B",
                    11 => "Factura C",
                    _ => $"Tipo {comprobante.TipoComprobante}"
                };

                // Generar QR como imagen
                byte[]? qrImageBytes = null;
                if (!string.IsNullOrEmpty(comprobante.CAE))
                {
                    try
                    {
                        var cuitServicio = _afipService.GetCuitServicio();
                        var tipoDocRec = string.IsNullOrEmpty(comprobante.CuitCliente) ? 99 : 80;
                        var nroDocRec = string.IsNullOrEmpty(comprobante.CuitCliente) ? 0 : long.Parse(comprobante.CuitCliente.Replace("-", ""));

                        var datosQR = $"{{\"ver\":1,\"fecha\":\"{comprobante.FechaEmision:yyyy-MM-dd}\"," +
                                     $"\"cuit\":{cuitServicio}," +
                                     $"\"ptoVta\":{comprobante.PuntoVenta}," +
                                     $"\"tipoCmp\":{comprobante.TipoComprobante}," +
                                     $"\"nroCmp\":{comprobante.NumeroComprobante}," +
                                     $"\"importe\":{comprobante.ImporteTotal:F2}," +
                                     $"\"moneda\":\"PES\",\"ctz\":1," +
                                     $"\"tipoDocRec\":{tipoDocRec},\"nroDocRec\":{nroDocRec}," +
                                     $"\"tipoCodAut\":\"E\",\"codAut\":{comprobante.CAE}}}";

                        using var qrGenerator = new global::QRCoder.QRCodeGenerator();
                        var qrData = qrGenerator.CreateQrCode(datosQR, global::QRCoder.QRCodeGenerator.ECCLevel.Q);
                        using var qrCode = new global::QRCoder.PngByteQRCode(qrData);
                        qrImageBytes = qrCode.GetGraphic(20);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "No se pudo generar QR para el PDF");
                    }
                }

                // Generar documento PDF térmico (80mm de ancho)
                var document = Document.Create(container =>
                {
                    container.Page(page =>
                    {
                        // 80mm de ancho, altura automática
                        // En QuestPDF, usamos Unit.Millimetre para especificar en milímetros
                        page.Size(80, 297, Unit.Millimetre); // 80mm ancho, 297mm alto (A4)
                        page.Margin(8, Unit.Millimetre);
                        page.DefaultTextStyle(x => x.FontSize(9));

                        page.Content().Column(col =>
                        {
                            // Encabezado
                            col.Item().AlignCenter().Text("ESTACIONAMIENTO DEMO").Bold().FontSize(12);
                            col.Item().AlignCenter().Text("CUIT 20-00000000-0");
                            col.Item().AlignCenter().Text("Av. Ejemplo 123 - Ciudad");
                            col.Item().PaddingBottom(5);
                            col.Item().AlignCenter().Text("-----------------------------------");

                            // Datos del comprobante
                            col.Item().PaddingTop(3).Text($"Comprobante: {tipoComprobanteTexto} N° {comprobante.PuntoVenta:D4}-{comprobante.NumeroComprobante ?? "Pendiente"}").Bold();
                            col.Item().Text($"Fecha: {comprobante.FechaEmision:dd/MM/yyyy HH:mm}");
                            col.Item().Text($"Cliente: {comprobante.RazonSocialCliente ?? "Consumidor Final"}");
                            col.Item().Text($"CUIT: {comprobante.CuitCliente ?? "-"}");
                            col.Item().PaddingBottom(3);
                            col.Item().AlignCenter().Text("-----------------------------------");

                            // Detalles
                            foreach (var det in comprobante.Detalles)
                            {
                                col.Item().PaddingTop(2).Text($"{det.Descripcion ?? "Sin descripción"}").Bold();
                                col.Item().Text($"Cant: {det.Cantidad:N2}   P.Unit: ${det.PrecioUnitario:N2}");
                                col.Item().Text($"IVA: {det.AlicuotaIVA:N2}%   Subtotal: ${det.Subtotal:N2}");
                                col.Item().PaddingBottom(2);
                                col.Item().AlignCenter().Text("-----------------------------------");
                            }

                            // Totales
                            col.Item().PaddingTop(5);
                            col.Item().Text($"Subtotal Neto: ${comprobante.ImporteNeto:N2}");
                            col.Item().Text($"IVA: ${comprobante.ImporteIVA:N2}");
                            col.Item().PaddingTop(3);
                            col.Item().AlignRight().Text($"TOTAL: ${comprobante.ImporteTotal:N2}").Bold().FontSize(11);
                            col.Item().PaddingBottom(3);
                            col.Item().AlignCenter().Text("===================================");

                            // Datos AFIP
                            if (!string.IsNullOrEmpty(comprobante.CAE))
                            {
                                col.Item().PaddingTop(5);
                                col.Item().Text($"CAE: {comprobante.CAE}").Bold();
                                if (comprobante.FechaVtoCAE.HasValue)
                                {
                                    col.Item().Text($"Vto CAE: {comprobante.FechaVtoCAE.Value:dd/MM/yyyy}");
                                }
                                col.Item().Text($"Estado: {comprobante.Estado}");
                                col.Item().PaddingBottom(3);
                            }

                            // Código QR
                            if (qrImageBytes != null && qrImageBytes.Length > 0)
                            {
                                col.Item().PaddingTop(5);
                                col.Item().AlignCenter().Text("Código de Autenticación AFIP").FontSize(8);
                                col.Item().AlignCenter().Image(qrImageBytes).FitWidth();
                                col.Item().AlignCenter().Text("Escanea para verificar").FontSize(7);
                                col.Item().PaddingBottom(3);
                            }

                            // Pie
                            col.Item().PaddingTop(5);
                            col.Item().AlignCenter().Text("===================================");
                            if (comprobante.Estado == "Autorizado" && !string.IsNullOrEmpty(comprobante.Observaciones) && comprobante.Observaciones.Contains("DEMO"))
                            {
                                col.Item().AlignCenter().Text("Comprobante autorizado en modo DEMO").FontSize(8);
                            }
                            else
                            {
                                col.Item().AlignCenter().Text("Comprobante autorizado por AFIP").FontSize(8);
                            }
                            col.Item().AlignCenter().Text(DateTime.Now.ToString("dd/MM/yyyy HH:mm")).FontSize(8);
                            col.Item().PaddingTop(3);
                            col.Item().AlignCenter().Text("¡Gracias por su visita!").FontSize(9);
                        });
                    });
                });

                // Generar archivo PDF en memoria
                var pdfBytes = document.GeneratePdf();

                // Retornar archivo para descargar o abrir en el navegador
                string nombreArchivo = $"Factura_{comprobante.PuntoVenta:D4}-{comprobante.NumeroComprobante ?? "Pendiente"}.pdf";
                return File(pdfBytes, "application/pdf", nombreArchivo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al generar PDF para comprobante {Id}", id);
                TempData["Error"] = $"Error al generar PDF: {ex.Message}";
                return RedirectToAction(nameof(Ver), new { id });
            }
        }

        // GET: Facturacion/ConsultarCliente
        [HttpGet]
        public async Task<IActionResult> ConsultarCliente(string cuit)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cuit))
                {
                    return Json(new { error = "CUIT requerido" });
                }

                // Limpiar CUIT (quitar guiones)
                cuit = cuit.Replace("-", "").Trim();

                if (cuit.Length != 11 || !cuit.All(char.IsDigit))
                {
                    return Json(new { error = "CUIT inválido" });
                }

                var persona = await _afipService.ConsultarPersonaAsync(cuit);

                // Mapear CondicionIVA a CondicionIVAReceptor (valores diferentes según AFIP)
                // CondicionIVA: 1=RI, 2=RNI, 3=Exento, 4=CF, 5=Monotributo, 6=SNC, 7=NR, 8=Monotributo Social, 9=PCE, 10=PCE Social, 11=EDE
                // CondicionIVAReceptor: 1=RI, 2=RI-Agente, 3=Exento, 4=Monotributo, 5=Consumidor Final, 6=No alcanzado, 7=Monotributo Social
                int? condicionIVAReceptorMapeado = MapearCondicionIVAReceptor(persona.CondicionIVA);

                // Si hay un error pero es de padrón en Homologación, no tratarlo como error crítico
                // Permitir que el usuario continúe con la facturación
                bool esErrorPadronHomologacion = persona.RazonSocial.Contains("no está disponible en el padrón de Homologación") ||
                                                 persona.RazonSocial.Contains("No existe persona con ese Id");
                
                // Si es error de padrón en Homologación, devolver como "error" pero informativo
                if (esErrorPadronHomologacion)
                {
                    return Json(new
                    {
                        error = persona.RazonSocial, // Mensaje informativo
                        cuit = persona.Cuit,
                        razonSocial = "", // Vacío para permitir ingreso manual
                        estado = persona.Estado,
                        tipoPersona = persona.TipoPersona,
                        condicionIVA = persona.CondicionIVA,
                        condicionIVAReceptor = condicionIVAReceptorMapeado,
                        esErrorPadronHomologacion = true // Flag para el frontend
                    });
                }
                
                // Si hay otro tipo de error, devolver como error
                if (persona.RazonSocial.Contains("Error") || string.IsNullOrEmpty(persona.RazonSocial))
                {
                    return Json(new
                    {
                        error = persona.RazonSocial,
                        cuit = persona.Cuit
                    });
                }

                // Consulta exitosa
                return Json(new
                {
                    cuit = persona.Cuit,
                    razonSocial = persona.RazonSocial,
                    estado = persona.Estado,
                    tipoPersona = persona.TipoPersona,
                    condicionIVA = persona.CondicionIVA,
                    condicionIVAReceptor = condicionIVAReceptorMapeado
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al consultar cliente");
                
                // Mensaje más descriptivo según el tipo de error
                string mensajeError = ex.Message.Contains("Certificado") 
                    ? "Error de certificado: Verifique la configuración en appsettings.json. Para modo demo, active 'ModoDemo: true'"
                    : $"Error al consultar AFIP: {ex.Message}";
                
                return Json(new { error = mensajeError });
            }
        }

        /// <summary>
        /// Mapea CondicionIVA del padrón a CondicionIVAReceptor para WSFE
        /// </summary>
        /// <param name="condicionIVA">Condición IVA del padrón (1-11)</param>
        /// <returns>Condición IVA Receptor para WSFE (1-7) o null si no se puede mapear</returns>
        private int? MapearCondicionIVAReceptor(int condicionIVA)
        {
            if (condicionIVA <= 0)
                return null;

            return condicionIVA switch
            {
                1 => 1,  // RI -> RI
                2 => 1,  // RNI -> RI (aproximación)
                3 => 3,  // Exento -> Exento
                4 => 5,  // CF -> Consumidor Final
                5 => 4,  // Monotributo -> Monotributo
                6 => 6,  // SNC -> No alcanzado (aproximación)
                7 => 6,  // NR -> No alcanzado
                8 => 7,  // Monotributo Social -> Monotributo Social
                _ => 5   // Por defecto: Consumidor Final
            };
        }

        // Método auxiliar para calcular totales
        // ⚠️ NOTA: Este método solo se usa si los valores vienen en 0 (fallback)
        // Normalmente el JS ya calcula correctamente y envía los valores
        private void CalcularTotales(Comprobante comprobante)
        {
            decimal importeNeto = 0;
            decimal importeIVA = 0;

            // ⚠️ CRÍTICO: Usar las propiedades calculadas de DetalleComprobante
            // Estas propiedades ahora usan la fórmula correcta (precio NETO)
            foreach (var detalle in comprobante.Detalles)
            {
                // Las propiedades ImporteNeto e ImporteIVA ya están calculadas correctamente
                // ImporteNeto = Cantidad * PrecioUnitario (precio es NETO)
                // ImporteIVA = ImporteNeto * (AlicuotaIVA / 100)
                importeNeto += detalle.ImporteNeto;
                importeIVA += detalle.ImporteIVA;
            }

            comprobante.ImporteNeto = Math.Round(importeNeto, 2);
            comprobante.ImporteIVA = Math.Round(importeIVA, 2);
            comprobante.ImporteTotal = Math.Round(importeNeto + importeIVA, 2);
            
            _logger.LogInformation(
                "🔄 CalcularTotales (fallback) -> ImporteNeto: {Neto}, ImporteIVA: {IVA}, ImporteTotal: {Total}",
                comprobante.ImporteNeto,
                comprobante.ImporteIVA,
                comprobante.ImporteTotal
            );
        }
    }
}

