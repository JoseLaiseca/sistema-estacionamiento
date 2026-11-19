using Microsoft.AspNetCore.Mvc;
using EstacionamientoWebApp.Services.WSAA;
using Microsoft.Extensions.Logging;

namespace EstacionamientoWebApp.Controllers
{
    public class WSAATestController : Controller
    {
        private readonly WSAAService _wsaa;
        private readonly ILogger<WSAATestController> _logger;

        public WSAATestController(WSAAService wsaa, ILogger<WSAATestController> logger)
        {
            _wsaa = wsaa;
            _logger = logger;
        }

        [HttpGet("/wsaa/test/wsfe")]
        public async Task<IActionResult> TestWSFE()
        {
            var result = await _wsaa.ObtenerCredencialesAsync("wsfe");
            return Json(result);
        }

        [HttpGet("/wsaa/test/padron")]
        public async Task<IActionResult> TestPadron()
        {
            var result = await _wsaa.ObtenerCredencialesAsync("ws_sr_padron_a5");
            return Json(result);
        }
    }
}

