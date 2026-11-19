using EstacionamientoWebApp.Models;
using EstacionamientoWebApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;
using ZXing;
using ZXing.Common;



namespace EstacionamientoWebApp.Controllers
{
    // 🔹 DTOs para el cálculo de cobro por código
    public class CodigoRequest
    {
        public string codigo { get; set; } = string.Empty;
    }

    public class CalculoResponse
    {
        public bool ok { get; set; }
        public string mensaje { get; set; } = string.Empty;
        public string tipo { get; set; } = string.Empty;
        public string patente { get; set; } = string.Empty;
        public string horaEntrada { get; set; } = string.Empty;
        public string horaActual { get; set; } = string.Empty;
        public decimal monto { get; set; }
        public double minutos { get; set; }
        public string horas { get; set; } = string.Empty;

    }
    [Authorize]
    public class IngresoController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly ITarifasService _tarifas; // ✅ agregamos esta

        // Tarifas por hora y tolerancias
        private static readonly Dictionary<int, (decimal precioHora, decimal extraTolerancia)> TarifasPorTipo = new()
        {
            { 1, (1500m, 500m) }, // Auto: $1,500/hora + $500 extra (5-20min)
            { 2, (2000m, 500m) }, // Camioneta: $2,000/hora + $1,000 extra (5-20min)
            { 3, ( 1000m, 0) }, // Moto: $1000/hora 
            { 4, (2300m, 500m) }, // Combi: $2,300/hora + $500 extra (5-20min)
        };

        // Nombre por tipo
        private static string GetNombreTipo(int tipoId) => tipoId switch
        {
            1 => "Auto",
            2 => "Camioneta",
            3 => "Moto",
            4 => "Combi",
            _ => "Desconocido"
        };

        private static int GetTramoIndex(int minutos)
        {
            if (minutos <= 65) return 1;       // Tramo 1
            if (minutos <= 75) return 2;       // Tramo 2
            if (minutos <= 120) return 3;      // Tramo 3 (2 horas)
                                               // Si supera los tramos definidos, cae a bloques de 60' a partir de acá:
            return 0; // 0 = fuera de tramos fijos
        }

        private static decimal CalcularMontoFlexible(int tipoId, DateTime entrada, DateTime salida)
        {
            if (salida < entrada) entrada = entrada.AddDays(-1);

            var totalMin = (salida - entrada).TotalMinutes;
            if (totalMin < 0) totalMin += 1440;
            int minutos = (int)Math.Floor(totalMin + 0.0001);

            if (!TarifasPorTipo.ContainsKey(tipoId))
                return 0m;

            var tarifas = TarifasPorTipo[tipoId]; // ✅ declarar una sola vez

            // Lógica especial para motos
            if (tipoId == 3)
            {
                // Para motos: $1000 por hora, pero con lógica sucesiva y tolerancia
                int horasMoto = minutos / 60;
                int minutosMoto = minutos % 60;
                
                // Si no hay horas completas, cobrar mínimo 1 hora
                if (horasMoto == 0)
                    return 1000m;
                
                // Calcular monto base por horas completas
                decimal montoMoto = horasMoto * 1000m;
                
                // Aplicar tolerancia de 15 minutos antes de cobrar la hora siguiente
                if (minutosMoto <= 15)
                {
                    // Tolerancia de 15 minutos: no se cobra extra
                    return montoMoto;
                }
                else
                {
                    // Supera la tolerancia: se cobra la hora siguiente
                    return montoMoto + 1000m;
                }
            }

            // Lógica original para otros vehículos
            var precioHora = tarifas.precioHora;
            var extraTolerancia = tarifas.extraTolerancia;

            int horasCompletas = minutos / 60;
            int minutosRestantes = minutos % 60;

            if (horasCompletas == 0)
                return precioHora;

            decimal montoBase = horasCompletas * precioHora;

            if (minutosRestantes <= 5)
                return montoBase;
            else if (minutosRestantes <= 15)
                return montoBase + extraTolerancia;
            else
                return montoBase + precioHora;
        }

        //private static decimal CalcularMontoFlexible(int tipoId, DateTime entrada, DateTime salida)
        //{
        //    if (salida < entrada) entrada = entrada.AddDays(-1); // cruzó medianoche

        //    var totalMin = (salida - entrada).TotalMinutes;
        //    if (totalMin < 0) totalMin += 1440;

        //    int minutos = (int)Math.Floor(totalMin + 0.0001);

        //    if (!TarifasPorTipo.ContainsKey(tipoId))
        //        return 0m;

        //    // Lógica especial para motos (tipoId = 3)
        //    if (tipoId == 3) // Moto
        //    {
        //        if (minutos <= 75)
        //        {
        //            // Hasta 75 minutos: $1000 fijo
        //            return 1000m;
        //        }
        //        else
        //        {
        //            // Después de 75 minutos: se cobra como 2 horas completas
        //            var tarifas = TarifasPorTipo[tipoId];
        //            return 2 * tarifas.precioHora; // 2 horas
        //        }
        //    }

        //    // Lógica original para otros vehículos (Auto y Camioneta)
        //    var tarifas = TarifasPorTipo[tipoId];
        //    var precioHora = tarifas.precioHora;
        //    var extraTolerancia = tarifas.extraTolerancia;

        //    // Calcular horas completas y minutos restantes
        //    int horasCompletas = minutos / 60;
        //    int minutosRestantes = minutos % 60;

        //    // Si no hay horas completas, cobrar mínimo 1 hora
        //    if (horasCompletas == 0)
        //        return precioHora;

        //    // Calcular monto base por horas completas
        //    decimal montoBase = horasCompletas * precioHora;

        //    // Aplicar tolerancia de 5-20 minutos
        //    if (minutosRestantes <= 5)
        //    {
        //        // Tolerancia de 5 minutos: no se cobra extra
        //        return montoBase;
        //    }
        //    else if (minutosRestantes <= 15)
        //    {
        //        // Tolerancia de 20 minutos: se cobra extra
        //        return montoBase + extraTolerancia;
        //    }
        //    else
        //    {
        //        // Supera la tolerancia: se cobra la hora siguiente
        //        return montoBase + precioHora;
        //    }
        //}


        // 🔹 Helper para mapear desde el texto de BD al ID
        private static int GetTipoId(string nombre)
        {
            if (string.IsNullOrWhiteSpace(nombre)) return 0;
            nombre = nombre.Trim().ToLowerInvariant();
            return nombre switch
            {
                "auto" => 1,
                "camioneta" => 2,
                "moto" => 3,
                "combi" => 4,
                _ => 0
            };
        }

        public IngresoController(ApplicationDbContext context, ITarifasService tarifas)
        {
            _context = context;
            _tarifas = tarifas; 
        }
        [HttpGet]
        public async Task<IActionResult> TestTarifa(DateTime entrada, DateTime salida)
        {
            var (importe, minutos, tramo) = await _tarifas.CalcularImporteAsync(entrada, salida);
            return Ok(new
            {
                Entrada = entrada,
                Salida = salida,
                Minutos = minutos,
                Importe = importe,
                TramoId = tramo?.Id,
                Rango = tramo is null ? null : $"{tramo.MinutosDesde}-{tramo.MinutosHasta}"
            });
        }

        // Método de prueba para verificar el cálculo de tarifas de motos
        [HttpGet]
        public IActionResult TestMotoTarifa(int minutos)
        {
            DateTime entrada = DateTime.Now.AddMinutes(-minutos);
            DateTime salida = DateTime.Now;
            
            decimal monto = CalcularMontoFlexible(3, entrada, salida); // tipoId = 3 para moto
            
            return Ok(new
            {
                Minutos = minutos,
                HorasCompletas = minutos / 60,
                MinutosRestantes = minutos % 60,
                MontoCalculado = monto,
                Descripcion = $"Para {minutos} minutos: {minutos / 60} horas completas + {minutos % 60} minutos restantes = ${monto}"
            });
        }

        // ✅ Genera un PNG de prueba del código de barras
        [HttpGet]
        public IActionResult GenerarPruebaCodigo()
        {
            string datosBarras = "1|ABC123|202508071530"; // Ejemplo

            var codigoBarrasBytes = GenerarCodigoBarras(datosBarras, widthPx: 220, heightPx: 50, margin: 8);

            Directory.CreateDirectory("C:\\Temp");
            System.IO.File.WriteAllBytes("C:\\Temp\\codigo_prueba.png", codigoBarrasBytes);

            return Ok("Código de prueba guardado en C:\\Temp\\codigo_prueba.png");
        }
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public IActionResult CalcularCobroCodigo([FromBody] CodigoRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.codigo))
                return Json(new CalculoResponse { ok = false, mensaje = "No se recibió código." });

            string codigo = req.codigo.Trim().ToUpperInvariant();
            if (codigo.Length < 6)
                return Json(new CalculoResponse { ok = false, mensaje = "Código demasiado corto." });

            // TipoId es el primer dígito
            if (!int.TryParse(codigo.Substring(0, 1), out int tipoId) || !TarifasPorTipo.ContainsKey(tipoId))
                return Json(new CalculoResponse { ok = false, mensaje = "Tipo de vehículo inválido." });

            string nombreTipo = GetNombreTipo(tipoId);

            // Patente y hora HHmm
            string patente = codigo.Substring(1, codigo.Length - 5);
            string horaStr = codigo.Substring(codigo.Length - 4);
            if (!TimeSpan.TryParseExact(horaStr, "hhmm", null, out var horaEntradaTS))
                return Json(new CalculoResponse { ok = false, mensaje = "Hora inválida en el código." });

            DateTime horaEntrada = DateTime.Today.Add(horaEntradaTS);
            DateTime horaActual = DateTime.Now;
            if (horaActual < horaEntrada) horaEntrada = horaEntrada.AddDays(-1);

            // Calcular monto con la lógica nueva
            decimal monto = CalcularMontoFlexible(tipoId, horaEntrada, horaActual);

            // Datos informativos
            double totalMin = (horaActual - horaEntrada).TotalMinutes;
            if (totalMin < 0) totalMin += 1440;
            
            int horasInt = (int)(totalMin / 60);
            int minutosInt = (int)(totalMin % 60);

            string horasFormateado = $"{horasInt:D2}:{minutosInt:D2}";

            return Json(new CalculoResponse
            {
                ok = true,
                mensaje = "OK",
                tipo = nombreTipo,
                patente = patente,
                horaEntrada = horaEntrada.ToString("HH:mm"),
                horaActual = horaActual.ToString("HH:mm"),
                minutos = Math.Round(totalMin, 0),
                horas = horasFormateado,
                monto = monto
            });
        }


        // ✅ Genera el código de barras optimizado (con tamaño fijo)
        public byte[] GenerarCodigoBarras(string datos, int widthPx, int heightPx, int margin = 8)
        {
            var writer = new BarcodeWriterPixelData
            {
                Format = BarcodeFormat.CODE_128,
                Options = new EncodingOptions
                {
                    Height = heightPx,
                    Width = widthPx,
                    Margin = margin,
                    PureBarcode = true
                }
            };

            var pixelData = writer.Write(datos);

            using (var bitmap = new Bitmap(pixelData.Width, pixelData.Height, PixelFormat.Format32bppRgb))
            {
                var bitmapData = bitmap.LockBits(new Rectangle(0, 0, pixelData.Width, pixelData.Height),
                                                 ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(pixelData.Pixels, 0, bitmapData.Scan0, pixelData.Pixels.Length);
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                using (var ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        // ✅ Pantalla para leer código de barras y calcular cobro
        [HttpGet]
        public IActionResult Cobrar()
        {
            return View(new CobroModel());
        }
        [HttpPost]
        public IActionResult ProcesarCobro(string CodigoBarras)
        {
            if (string.IsNullOrWhiteSpace(CodigoBarras))
            {
                TempData["Error"] = "No se leyó el código de barras.";
                return RedirectToAction("Cobrar");
            }

            string codigo = CodigoBarras.Trim().ToUpperInvariant();
            if (codigo.Length < 6)
            {
                TempData["Error"] = "Código demasiado corto.";
                return RedirectToAction("Cobrar");
            }

            if (!int.TryParse(codigo.Substring(0, 1), out int tipoId) || !TarifasPorTipo.ContainsKey(tipoId))
            {
                TempData["Error"] = "Tipo de vehículo inválido en el código.";
                return RedirectToAction("Cobrar");
            }

            string nombreTipo = GetNombreTipo(tipoId);

            string patente = codigo.Substring(1, codigo.Length - 5);
            string horaStr = codigo.Substring(codigo.Length - 4);
            if (!TimeSpan.TryParseExact(horaStr, "hhmm", null, out var horaEntradaTS))
            {
                TempData["Error"] = "Hora inválida en el código.";
                return RedirectToAction("Cobrar");
            }

            DateTime horaEntrada = DateTime.Today.Add(horaEntradaTS);
            DateTime horaActual = DateTime.Now;
            if (horaActual < horaEntrada) horaEntrada = horaEntrada.AddDays(-1);

            decimal monto = CalcularMontoFlexible(tipoId, horaEntrada, horaActual);

            var model = new CobroModel
            {
                TipoVehiculo = nombreTipo,
                Patente = patente,
                HoraEntrada = horaEntrada.ToString("HH:mm"),
                HoraActual = horaActual.ToString("HH:mm"),
                Monto = monto
            };

            return View("Cobrar", model);
        }


        // ✅ Eliminar por ID
        [HttpDelete]
        public IActionResult Eliminar(int id)
        {
            try
            {
                var ingreso = _context.Ingresos.FirstOrDefault(x => x.Id == id);
                if (ingreso == null)
                {
                    return NotFound();
                }

                _context.Ingresos.Remove(ingreso);
                _context.SaveChanges();
                return Ok(new { mensaje = "Registro eliminado" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = $"Error al eliminar: {ex.Message}" });
            }
        }

        // ✅ Imprimir por ID (usa nuevo formato de barcode y tamaño fijo)
        [HttpGet]
        [SupportedOSPlatform("windows")]
        public IActionResult Imprimir(int id)
        {
            var ingreso = _context.Ingresos.FirstOrDefault(x => x.Id == id);
            if (ingreso == null)
            {
                return NotFound(new { mensaje = "No se encontró el registro." });
            }

            string textoTicket =
                $"   Playa Demo \n" +
                $"     Bazan y Bustos \n" +
                $"           y \n" +
                $"   Santiago del Estero\n" +
                $"     - Ciudad -\n" +
                $"   Horario de atención: \n" +
                $"     6:30 hs a 23:00 hs\n" +
                $"-------------------------------------\n" +
                $"Fecha: {ingreso.Fecha:dd/MM/yyyy}\n" +
                $"Hora: {ingreso.Fecha:HH:mm:ss}\n" +
                $"Tipo Vehículo: {ingreso.TipoVehiculo}\n" +
                $"Patente: {(string.IsNullOrEmpty(ingreso.Patente) ? "-" : ingreso.Patente)}\n" +
                $"-------------------------------------\n" +
                $" ¡Gracias por su visita!\n\n";

            string textoPequeno =
                "El cliente se notifica y acepta que\n" +
                "si el estacionamiento del vehiculo \n" +
                "en la playa no implica contrato de \n" +
                "deposito y que el propietario y/o\n" +
                "concesionario de la playa o persona\n" +
                "que trabajen en ella no se hacen \n" +
                "responsable por cualquier clase de\n" +
                "accidente o daño a los vehiculos o\n" +
                "persona por robo, hurto,etc. Que \n" +
                "pueden ser objetos de los mismos\n" +
                "o bienes que se encuentren en ellos\n" +
                "durante el tiempo de permanencia \n" +
                "\n" +
                "\n";

            string nombreImpresora = "POS-80C"; // Cambiar si tiene otro nombre en Windows

            try
            {
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

                        // 🔹 Código de barras: TipoId|Patente|yyyyMMddHHmm
                        int tipoId = GetTipoId(ingreso.TipoVehiculo);
                        string patenteLimpia = (ingreso.Patente ?? "-").Trim().ToUpperInvariant();
                        string horaCodificada = ingreso.Fecha.ToString("HHmm"); // solo hora y minutos
                        string datosBarras = $"{tipoId}{patenteLimpia}{horaCodificada}";

                        var codigoBarrasBytes = GenerarCodigoBarras(datosBarras, widthPx: 220, heightPx: 50, margin: 8);

                        using (var ms = new MemoryStream(codigoBarrasBytes))
                        using (var img = Image.FromStream(ms))
                        {
                            e.Graphics.DrawImage(img, new Rectangle(0, (int)y, img.Width, img.Height));
                            y += img.Height + 10;
                        }

                        // Texto chico
                        using (Font fuenteChica = new Font("Consolas", 7))
                        {
                            e.Graphics.DrawString(textoPequeno, fuenteChica, Brushes.Black, 0, y);
                        }
                    };

                    pd.Print();
                }

                return Ok(new { mensaje = "Ticket enviado a imprimir" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al imprimir: " + ex.Message });
            }
        }

        // 1️⃣ Imprime justo después de guardar el ingreso
        [HttpPost]
        [SupportedOSPlatform("windows")]
        public IActionResult ImprimirRecienIngresado(IngresoModel model)
        {
            try
            {
                model.Fecha = DateTime.Now;
                _context.Ingresos.Add(model);
                _context.SaveChanges();

                ImprimirTicket(model);
                TempData["Mensaje"] = "Ticket impreso y guardado correctamente.";
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Error al procesar: {ex.Message}";
                return RedirectToAction("Index");
            }
        }

        // 2️⃣ Imprime un ingreso ya guardado usando su ID
        [HttpGet]
        [SupportedOSPlatform("windows")]
        public IActionResult ImprimirGuardado(int id)
        {
            var ingreso = _context.Ingresos.FirstOrDefault(i => i.Id == id);
            if (ingreso == null)
            {
                return NotFound("No se encontró el registro.");
            }

            ImprimirTicket(ingreso);
            return Ok(new { mensaje = "Ticket enviado a imprimir" });
        }

        // Método privado para no repetir código de impresión
        private void ImprimirTicket(IngresoModel ingreso)
        {
            string textoTicket =
                $"   Playa Demo \n" +
                $"     Bazan y Bustos \n" +
                $"           y \n" +
                $"   Santiago del Estero\n" +
                $"     - Ciudad -\n" +
                $"   Horario de atención: \n" +
                $"     6:30 hs a 23:00 hs\n" +
                $"-------------------------------------\n" +
                $"Fecha: {ingreso.Fecha:dd/MM/yyyy}\n" +
                $"Hora: {ingreso.Fecha:HH:mm:ss}\n" +
                $"Tipo Vehículo: {ingreso.TipoVehiculo}\n" +
                $"Patente: {(string.IsNullOrEmpty(ingreso.Patente) ? "-" : ingreso.Patente)}\n" +
                $"-------------------------------------\n" +
                $" ¡Gracias por su visita!\n\n";

            string textoPequeno =
                "El cliente se notifica y acepta que\n" +
                "si el estacionamiento del vehiculo \n" +
                "en la playa no implica contrato de \n" +
                "deposito y que el propietario y/o\n" +
                "concesionario de la playa o persona\n" +
                "que trabajen en ella no se hacen \n" +
                "responsable por cualquier clase de\n" +
                "accidente o daño a los vehiculos o\n" +
                "persona por robo, hurto,etc. Que \n" +
                "pueden ser objetos de los mismos\n" +
                "o bienes que se encuentren en ellos\n" +
                "durante el tiempo de permanencia \n\n\n" +
                "\n" +
                "\n";

            using (PrintDocument pd = new PrintDocument())
            {
                pd.PrinterSettings.PrinterName = "POS-80C";

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

                    // 🔹 Código de barras: TipoId|Patente|yyyyMMddHHmm
                    int tipoId = GetTipoId(ingreso.TipoVehiculo);
                    string patenteLimpia = (ingreso.Patente ?? "-").Trim().ToUpperInvariant();
                    string horaCodificada = ingreso.Fecha.ToString("HHmm"); // solo hora y minutos
                    string datosBarras = $"{tipoId}{patenteLimpia}{horaCodificada}";


                    var codigoBarrasBytes = GenerarCodigoBarras(datosBarras, widthPx: 220, heightPx: 50, margin: 8);

                    using (var ms = new MemoryStream(codigoBarrasBytes))
                    using (var img = Image.FromStream(ms))
                    {
                        e.Graphics.DrawImage(img, new Rectangle(0, (int)y, img.Width, img.Height));
                        y += img.Height + 10;
                    }

                    // Texto chico
                    using (Font fuenteChica = new Font("Consolas", 7))
                    {
                        e.Graphics.DrawString(textoPequeno, fuenteChica, Brushes.Black, 0, y);
                    }
                };

                pd.Print();
            }
        }

        [HttpPost]
        public IActionResult Guardar(IngresoModel model)
        {
            try
            {
                if (ModelState.IsValid)
                {
                    model.Fecha = DateTime.Now;
                    _context.Ingresos.Add(model);
                    _context.SaveChanges();

                    TempData["Mensaje"] = "Datos guardados correctamente.";
                    return RedirectToAction("Index");
                }

                TempData["Error"] = "Datos inválidos.";
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Error al guardar: {ex.Message}";
                return RedirectToAction("Index");
            }
        }

        // ✅ Vista principal con filtro de fecha
        public async Task<IActionResult> Index(DateTime? fecha)
        {
            var fechaFiltro = (fecha ?? DateTime.Today).Date;

            var ingresosFiltrados = await _context.Ingresos
                .Where(i => i.Fecha.Date == fechaFiltro)
                .OrderByDescending(i => i.Fecha)
                .Select(i => new IngresoModel
                {
                    Id = i.Id,
                    Patente = i.Patente,
                    TipoVehiculo = i.TipoVehiculo,
                    Descripcion = i.Descripcion,
                    Fecha = i.Fecha
                })
                .ToListAsync();

            var mensualesActivos = await _context.Mensuales
                .Where(m => m.Activo && !m.Eliminado)
                .OrderByDescending(m => m.Fecha)
                .ToListAsync();

            var viewModel = new IngresoViewModel
            {
                Ingreso = new IngresoModel(),
                Ingresos = ingresosFiltrados,
                FechaFiltro = fechaFiltro, // guarda la fecha usada
                Mensual = new Mensual(),
                Mensuales = mensualesActivos
            };

            return View(viewModel);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult GuardarMensual([Bind(Prefix = "Mensual")] Mensual model)
        {
            try
            {
                Console.WriteLine($"[GuardarMensual] Patente={model?.Patente}, Nombre={model?.Nombre}");

                if (model == null)
                {
                    TempData["Error"] = "Datos inválidos.";
                    return RedirectToAction("Index");
                }

                if (!ModelState.IsValid)
                {
                    foreach (var kv in ModelState)
                        foreach (var err in kv.Value.Errors)
                            Console.WriteLine($"ModelStateError {kv.Key}: {err.ErrorMessage}");

                    TempData["Error"] = "Datos inválidos.";
                    return RedirectToAction("Index");
                }

                model.Patente = (model.Patente ?? "").Trim().ToUpperInvariant();
                model.Fecha = DateTime.Now;
                model.FechaVencimiento = model.Fecha.AddMonths(1);
                model.Activo = true;
                model.Eliminado = false;

                model.DejoLlave = false;

                _context.Mensuales.Add(model);
                _context.SaveChanges();

                TempData["Mensaje"] = "Mensual guardado correctamente.";
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Error al guardar mensual: {ex.Message}";
                return RedirectToAction("Index");
            }
        }


        // ✅ Eliminar mensual por ID (marcar como eliminado)
        [HttpDelete]
        public IActionResult EliminarMensual(int id)
        {
            var mensual = _context.Mensuales.FirstOrDefault(x => x.Id == id);
            if (mensual == null)
            {
                return NotFound();
            }

            mensual.Eliminado = true;
            mensual.Activo = false;
            _context.SaveChanges();
            return Ok(new { mensaje = "Mensual eliminado" });
        }

        // ✅ Desactivar mensual (en lugar de eliminar)
        [HttpPost]
        public IActionResult DesactivarMensual(int id)
        {
            var mensual = _context.Mensuales.FirstOrDefault(x => x.Id == id);
            if (mensual == null)
            {
                return NotFound();
            }

            mensual.Activo = false;
            _context.SaveChanges();
            return Ok(new { mensaje = "Mensual desactivado" });
        }

        // ===== NUEVOS ENDPOINTS PARA MENSUALES REGISTRADOS =====

        // ✅ Obtener todos los mensuales (para la nueva pestaña)
        [HttpGet]
        public async Task<IActionResult> ObtenerMensuales(string? estado, string? patente, string? cliente)
        {
            var query = _context.Mensuales.AsQueryable();

            // Aplicar filtros
            if (!string.IsNullOrEmpty(estado))
            {
                switch (estado.ToLower())
                {
                    case "activo":
                        query = query.Where(m => m.Activo && !m.Eliminado);
                        break;
                    case "inactivo":
                        query = query.Where(m => !m.Activo && !m.Eliminado);
                        break;
                    case "vencido":
                        query = query.Where(m => m.Activo && !m.Eliminado && m.FechaVencimiento < DateTime.Now);
                        break;
                }
            }
            else
            {
                // Por defecto, mostrar todos los no eliminados
                query = query.Where(m => !m.Eliminado);
            }

            if (!string.IsNullOrEmpty(patente))
            {
                query = query.Where(m => m.Patente.Contains(patente.ToUpper()));
            }

            if (!string.IsNullOrEmpty(cliente))
            {
                query = query.Where(m => m.Nombre.Contains(cliente));
            }

            var mensuales = await query
                .OrderByDescending(m => m.Id)
                .Select(m => new
                {
                    m.Id,
                    m.Vehiculo,
                    m.Patente,
                    m.Nombre,
                    m.Precio,
                    m.Descripcion,
                    Fecha = m.Fecha,
                    FechaVencimiento = m.FechaVencimiento,
                    m.Activo,
                    m.Eliminado
                })
                .ToListAsync();

            return Json(mensuales);
        }

        // ✅ Obtener un mensual específico por ID
        [HttpGet]
        public async Task<IActionResult> ObtenerMensual(int id)
        {
            var mensual = await _context.Mensuales
                .Where(m => m.Id == id && !m.Eliminado)
                .Select(m => new
                {
                    m.Id,
                    m.Vehiculo,
                    m.Patente,
                    m.Nombre,
                    m.Precio,
                    m.Descripcion,
                    Fecha = m.Fecha,
                    FechaVencimiento = m.FechaVencimiento,
                    m.Activo,
                    m.Eliminado
                })
                .FirstOrDefaultAsync();

            if (mensual == null)
            {
                return NotFound(new { mensaje = "Mensual no encontrado" });
            }

            return Json(mensual);
        }

        // ✅ Imprimir ticket de cobro con monto y tiempo
        [HttpPost]
        [IgnoreAntiforgeryToken]
        [SupportedOSPlatform("windows")]
        public IActionResult ImprimirTicketCobro([FromBody] CalculoResponse datosCobro)
        {
            if (datosCobro == null)
            {
                return BadRequest(new { mensaje = "No se recibieron datos para imprimir." });
            }

            try
            {
                string textoTicket =
                    $"   Playa Demo \n" +
                    $"     Bazan y Bustos \n" +
                    $"           y \n" +
                    $"   Santiago del Estero\n" +
                    $"     - Ciudad -\n" +
                    $"   Horario de atención: \n" +
                    $"     6:30 hs a 23:00 hs\n" +
                    $"-------------------------------------\n" +
                    $"Fecha: {DateTime.Now:dd/MM/yyyy}\n" +
                    $"Hora: {DateTime.Now:HH:mm:ss}\n" +
                    $"Tipo Vehículo: {datosCobro.tipo}\n" +
                    $"Patente: {datosCobro.patente}\n" +
                    $"-------------------------------------\n" +
                    $"Hora Entrada: {datosCobro.horaEntrada}\n" +
                    $"Hora Salida: {datosCobro.horaActual}\n" +
                    $"Tiempo Total: {datosCobro.horas}\n" +
                    $"-------------------------------------\n" +
                    $"IMPORTE A PAGAR:\n" +
                    $"${datosCobro.monto:F2}\n" +
                    $"-------------------------------------\n" +
                    $" ¡Gracias por su visita!\n\n";

                string textoPequeno =
                    "El cliente se notifica y acepta que\n" +
                    "si el estacionamiento del vehiculo \n" +
                    "en la playa no implica contrato de \n" +
                    "deposito y que el propietario y/o\n" +
                    "concesionario de la playa o persona\n" +
                    "que trabajen en ella no se hacen \n" +
                    "responsable por cualquier clase de\n" +
                    "accidente o daño a los vehiculos o\n" +
                    "persona por robo, hurto,etc. Que \n" +
                    "pueden ser objetos de los mismos\n" +
                    "o bienes que se encuentren en ellos\n" +
                    "durante el tiempo de permanencia \n\n\n" +
                    "\n" +
                    "\n";

                using (PrintDocument pd = new PrintDocument())
                {
                    pd.PrinterSettings.PrinterName = "POS-80C";

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

                        // Código de barras removido del ticket de cobro

                        // Texto chico
                        using (Font fuenteChica = new Font("Consolas", 7))
                        {
                            e.Graphics.DrawString(textoPequeno, fuenteChica, Brushes.Black, 0, y);
                        }
                    };

                    pd.Print();
                }

                return Ok(new { mensaje = "Ticket de cobro enviado a imprimir correctamente" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al imprimir el ticket: " + ex.Message });
            }
        }

        // ===== ENDPOINTS PARA ESTADÍAS =====

        // ✅ Obtener todas las estadías
        [HttpGet]
        public async Task<IActionResult> ObtenerEstadias(string? patente, string? cliente, string? fecha)
        {
            var query = _context.Estadias.AsQueryable();

            // Aplicar filtros
            if (!string.IsNullOrEmpty(patente))
            {
                query = query.Where(e => e.Patente.Contains(patente.ToUpper()));
            }

            if (!string.IsNullOrEmpty(cliente))
            {
                query = query.Where(e => e.NombreCliente.Contains(cliente));
            }

            if (!string.IsNullOrEmpty(fecha) && DateTime.TryParse(fecha, out var fechaFiltro))
            {
                query = query.Where(e => e.FechaIngreso.Date == fechaFiltro.Date);
            }

            var estadias = await query
                .OrderByDescending(e => e.Id)
                .Select(e => new
                {
                    e.Id,
                    e.NombreCliente,
                    e.Celular,
                    e.Patente,
                    e.TipoVehiculo,
                    e.Descripcion,
                    e.DejoLlave,
                    e.DiasEstadia,
                    e.Monto,
                    FechaIngreso = e.FechaIngreso
                })
                .ToListAsync();

            return Json(estadias);
        }

        // ✅ Obtener una estadía específica por ID
        [HttpGet]
        public async Task<IActionResult> ObtenerEstadia(int id)
        {
            var estadia = await _context.Estadias
                .Where(e => e.Id == id)
                .Select(e => new
                {
                    e.Id,
                    e.NombreCliente,
                    e.Celular,
                    e.Patente,
                    e.TipoVehiculo,
                    e.Descripcion,
                    e.DejoLlave,
                    e.DiasEstadia,
                    e.Monto,
                    FechaIngreso = e.FechaIngreso
                })
                .FirstOrDefaultAsync();

            if (estadia == null)
            {
                return NotFound(new { mensaje = "Estadía no encontrada" });
            }

            return Json(estadia);
        }

        // ✅ Actualizar estadía
        [HttpPost]
        public async Task<IActionResult> ActualizarEstadia([FromBody] EstadiaModel estadia)
        {
            try
            {
                if (estadia == null)
                {
                    return BadRequest(new { mensaje = "No se recibieron datos" });
                }

                var estadiaExistente = await _context.Estadias.FirstOrDefaultAsync(e => e.Id == estadia.Id);
                if (estadiaExistente == null)
                {
                    return NotFound(new { mensaje = "Estadía no encontrada" });
                }

                // Actualizar campos
                estadiaExistente.NombreCliente = estadia.NombreCliente.Trim();
                estadiaExistente.Celular = estadia.Celular.Trim();
                estadiaExistente.Patente = estadia.Patente.Trim().ToUpperInvariant();
                estadiaExistente.TipoVehiculo = estadia.TipoVehiculo.Trim();
                estadiaExistente.Descripcion = string.IsNullOrWhiteSpace(estadia.Descripcion) ? null : estadia.Descripcion.Trim();
                estadiaExistente.DejoLlave = estadia.DejoLlave;
                estadiaExistente.DiasEstadia = estadia.DiasEstadia;
                estadiaExistente.Monto = estadia.Monto;

                await _context.SaveChangesAsync();
                return Ok(new { mensaje = "Estadía actualizada correctamente" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al actualizar la estadía: " + ex.Message });
            }
        }

        // ✅ Eliminar estadía por ID
        [HttpDelete]
        public async Task<IActionResult> EliminarEstadia(int id)
        {
            try
            {
                var estadia = await _context.Estadias.FirstOrDefaultAsync(e => e.Id == id);
                if (estadia == null)
                {
                    return NotFound(new { mensaje = "Estadía no encontrada" });
                }

                _context.Estadias.Remove(estadia);
                await _context.SaveChangesAsync();
                return Ok(new { mensaje = "Estadía eliminada correctamente" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al eliminar la estadía: " + ex.Message });
            }
        }

        // ✅ Guardar nueva estadía
        [HttpPost]
        public async Task<IActionResult> GuardarEstadia([Bind(Prefix = "Estadia")] EstadiaModel model)
        {
            try
            {
                if (!ModelState.IsValid)
                {
                    TempData["Error"] = "Datos inválidos.";
                    return RedirectToAction("Index");
                }

                model.Patente = (model.Patente ?? "").Trim().ToUpperInvariant();
                model.NombreCliente = (model.NombreCliente ?? "").Trim();
                model.Celular = (model.Celular ?? "").Trim();
                model.TipoVehiculo = (model.TipoVehiculo ?? "").Trim();
                model.Descripcion = string.IsNullOrWhiteSpace(model.Descripcion) ? null : model.Descripcion.Trim();
                model.FechaIngreso = DateTime.Now;

                _context.Estadias.Add(model);
                await _context.SaveChangesAsync();

                // Imprimir ticket automáticamente
                ImprimirTicketEstadia(model);

                TempData["Mensaje"] = "Estadía guardada e impresa correctamente.";
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                TempData["Error"] = "Error al guardar la estadía: " + ex.Message;
                return RedirectToAction("Index");
            }
        }

        // ✅ Método privado para imprimir ticket de estadía
        private void ImprimirTicketEstadia(EstadiaModel estadia)
        {
            try
            {
                string textoTicket =
                    $"   Playa Demo \n" +
                    $"     Bazan y Bustos \n" +
                    $"           y \n" +
                    $"   Santiago del Estero\n" +
                    $"     - Ciudad -\n" +
                    $"   Horario de atención: \n" +
                    $"     6:30 hs a 23:00 hs\n" +
                    $"-------------------------------------\n" +
                    $"Fecha: {estadia.FechaIngreso:dd/MM/yyyy}\n" +
                    $"Hora: {estadia.FechaIngreso:HH:mm:ss}\n" +
                    $"-------------------------------------\n" +
                    $"ESTADÍA PROLONGADA\n" +
                    $"-------------------------------------\n" +
                    $"Cliente: {estadia.NombreCliente}\n" +
                    $"Tipo: {estadia.TipoVehiculo}\n" +
                    $"Días: {estadia.DiasEstadia}\n" +
                    $"Monto: ${estadia.Monto:F2}\n" +
                    $"Llave: {(estadia.DejoLlave ? "Sí" : "No")}\n" +
                    $"-------------------------------------\n" +
                    $" ¡Gracias por su visita!\n\n";

                string textoPequeno =
                    "El cliente se notifica y acepta que\n" +
                    "si el estacionamiento del vehiculo \n" +
                    "en la playa no implica contrato de \n" +
                    "deposito y que el propietario y/o\n" +
                    "concesionario de la playa o persona\n" +
                    "que trabajen en ella no se hacen \n" +
                    "responsable por cualquier clase de\n" +
                    "accidente o daño a los vehiculos o\n" +
                    "persona por robo, hurto,etc. Que \n" +
                    "pueden ser objetos de los mismos\n" +
                    "o bienes que se encuentren en ellos\n" +
                    "durante el tiempo de permanencia \n\n\n" +
                    "\n" +
                    "\n";

                using (PrintDocument pd = new PrintDocument())
                {
                    pd.PrinterSettings.PrinterName = "POS-80C";

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

                        // Texto chico
                        using (Font fuenteChica = new Font("Consolas", 7))
                        {
                            e.Graphics.DrawString(textoPequeno, fuenteChica, Brushes.Black, 0, y);
                        }
                    };

                    pd.Print();
                }
            }
            catch (Exception ex)
            {
                // Log del error pero no interrumpir el flujo
                Console.WriteLine($"Error al imprimir ticket de estadía: {ex.Message}");
            }
        }

        // ✅ Endpoint para imprimir estadía por ID
        [HttpGet]
        [SupportedOSPlatform("windows")]
        public async Task<IActionResult> ImprimirEstadia(int id)
        {
            try
            {
                var estadia = await _context.Estadias.FirstOrDefaultAsync(e => e.Id == id);
                if (estadia == null)
                {
                    return NotFound(new { mensaje = "Estadía no encontrada" });
                }

                ImprimirTicketEstadia(estadia);
                return Ok(new { mensaje = "Ticket de estadía enviado a imprimir correctamente" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al imprimir el ticket: " + ex.Message });
            }
        }

        // ✅ Método privado para imprimir factura de mensual
        private void ImprimirFacturaMensual(Mensual mensual)
        {
            try
            {
                string textoFactura =
                    $"   Playa Demo \n" +
                    $"     Bazan y Bustos \n" +
                    $"           y \n" +
                    $"   Santiago del Estero\n" +
                    $"     - Ciudad -\n" +
                    $"   Horario de atención: \n" +
                    $"     6:30 hs a 23:00 hs\n" +
                    $"-------------------------------------\n" +
                    $"FACTURA MENSUAL\n" +
                    $"-------------------------------------\n" +
                    $"Fecha: {DateTime.Now:dd/MM/yyyy}\n" +
                    $"Hora: {DateTime.Now:HH:mm:ss}\n" +
                    $"-------------------------------------\n" +
                    $"Cliente: {mensual.Nombre}\n" +
                    $"Vehículo: {mensual.Vehiculo}\n" +
                    $"Patente: {mensual.Patente}\n" +
                    $"Período: {mensual.Fecha:dd/MM/yyyy} al {mensual.FechaVencimiento:dd/MM/yyyy}\n" +
                    $"-------------------------------------\n" +
                    $"IMPORTE: ${mensual.Precio:F2}\n" +
                    $"-------------------------------------\n" +
                    $"Cobrado por: Sistema\n" +
                    $"Fecha de cobro: {DateTime.Now:dd/MM/yyyy}\n" +
                    $"-------------------------------------\n" +
                    $" ¡Gracias por su pago!\n\n";

                string textoPequeno =
                    "El cliente se notifica y acepta que\n" +
                    "si el estacionamiento del vehiculo \n" +
                    "en la playa no implica contrato de \n" +
                    "deposito y que el propietario y/o\n" +
                    "concesionario de la playa o persona\n" +
                    "que trabajen en ella no se hacen \n" +
                    "responsable por cualquier clase de\n" +
                    "accidente o daño a los vehiculos o\n" +
                    "persona por robo, hurto,etc. Que \n" +
                    "pueden ser objetos de los mismos\n" +
                    "o bienes que se encuentren en ellos\n" +
                    "durante el tiempo de permanencia \n\n\n" +
                    "\n" +
                    "\n";

                using (PrintDocument pd = new PrintDocument())
                {
                    pd.PrinterSettings.PrinterName = "POS-80C";
                        
                    pd.PrintPage += (sender, e) =>
                    {
                        if (e.Graphics == null) return;
                        float y = 0;

                        // Texto principal
                        using (Font fuenteNormal = new Font("Consolas", 10))
                        {
                            e.Graphics.DrawString(textoFactura, fuenteNormal, Brushes.Black, 0, y);
                            y += e.Graphics.MeasureString(textoFactura, fuenteNormal).Height;
                        }

                        // Texto chico
                        using (Font fuenteChica = new Font("Consolas", 7))
                        {
                            e.Graphics.DrawString(textoPequeno, fuenteChica, Brushes.Black, 0, y);
                        }
                    };

                    pd.Print();
                }
            }
            catch (Exception ex)
            {
                // Log del error pero no interrumpir el flujo
                Console.WriteLine($"Error al imprimir factura de mensual: {ex.Message}");
            }
        }

        // ✅ Endpoint para imprimir factura de mensual
        [HttpGet]
        [SupportedOSPlatform("windows")]
        public async Task<IActionResult> ImprimirMensual(int id)
        {
            try
            {
                var mensual = await _context.Mensuales.FirstOrDefaultAsync(m => m.Id == id);
                if (mensual == null)
                {
                    return NotFound(new { mensaje = "Mensual no encontrado" });
                }

                ImprimirFacturaMensual(mensual);
                return Ok(new { mensaje = "Factura de mensual enviada a imprimir correctamente" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al imprimir la factura: " + ex.Message });
            }
        }

        // ✅ Actualizar mensual
        [HttpPost]
        public async Task<IActionResult> ActualizarMensual([FromBody] JsonElement jsonElement)
        {
            try
            {
                // Validar que el JSON no esté vacío
                if (jsonElement.ValueKind == JsonValueKind.Null || jsonElement.ValueKind == JsonValueKind.Undefined)
                {
                    return BadRequest(new { mensaje = "No se recibieron datos" });
                }

                // Extraer datos del JsonElement de forma segura
                int id = jsonElement.TryGetProperty("Id", out var idElement) ? idElement.GetInt32() : 0;
                string vehiculo = jsonElement.TryGetProperty("Vehiculo", out var vehiculoElement) ? vehiculoElement.GetString() ?? "" : "";
                string patente = jsonElement.TryGetProperty("Patente", out var patenteElement) ? patenteElement.GetString() ?? "" : "";
                string nombre = jsonElement.TryGetProperty("Nombre", out var nombreElement) ? nombreElement.GetString() ?? "" : "";
                decimal precio = jsonElement.TryGetProperty("Precio", out var precioElement) ? precioElement.GetDecimal() : 0;
                string descripcion = jsonElement.TryGetProperty("Descripcion", out var descripcionElement) ? descripcionElement.GetString() ?? "" : "";
                DateTime fechaVencimiento = jsonElement.TryGetProperty("FechaVencimiento", out var fechaElement) ? fechaElement.GetDateTime() : DateTime.Now;
                bool activo = jsonElement.TryGetProperty("Activo", out var activoElement) ? activoElement.GetBoolean() : false;

                // Validaciones básicas
                if (id <= 0)
                {
                    return BadRequest(new { mensaje = "ID inválido" });
                }

                if (string.IsNullOrWhiteSpace(vehiculo))
                {
                    return BadRequest(new { mensaje = "El tipo de vehículo es requerido" });
                }

                if (string.IsNullOrWhiteSpace(patente))
                {
                    return BadRequest(new { mensaje = "La patente es requerida" });
                }

                if (string.IsNullOrWhiteSpace(nombre))
                {
                    return BadRequest(new { mensaje = "El nombre del cliente es requerido" });
                }

                if (precio < 0)
                {
                    return BadRequest(new { mensaje = "El precio no puede ser negativo" });
                }

                var mensual = await _context.Mensuales.FirstOrDefaultAsync(m => m.Id == id && !m.Eliminado);
                if (mensual == null)
                {
                    return NotFound(new { mensaje = "Mensual no encontrado" });
                }

                // Actualizar campos
                mensual.Vehiculo = vehiculo.Trim();
                mensual.Patente = patente.Trim().ToUpperInvariant();
                mensual.Nombre = nombre.Trim();
                mensual.Precio = precio;
                mensual.Descripcion = string.IsNullOrWhiteSpace(descripcion) ? null : descripcion.Trim();
                mensual.FechaVencimiento = fechaVencimiento;
                mensual.Activo = activo;

                await _context.SaveChangesAsync();
                return Ok(new { mensaje = "Mensual actualizado correctamente" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { mensaje = "Error al actualizar el mensual: " + ex.Message });
            }
        }

    }
}
