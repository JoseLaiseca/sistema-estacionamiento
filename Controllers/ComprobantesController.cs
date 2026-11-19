using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EstacionamientoWebApp.Models;
using System.Linq;
using System.Threading.Tasks;

namespace EstacionamientoWebApp.Controllers
{
    [Authorize]
    public class ComprobantesController : Controller
    {
        private readonly ApplicationDbContext _context;

        public ComprobantesController(ApplicationDbContext context)
        {
            _context = context;
        }

        // GET: /Comprobantes
        public async Task<IActionResult> Index()
        {
            var comprobantes = await _context.Comprobantes
                .Include(c => c.Detalles)
                .OrderByDescending(c => c.FechaEmision)
                .ToListAsync();

            return View(comprobantes);
        }

        // GET: /Comprobantes/Details/5
        public async Task<IActionResult> Details(int id)
        {
            var comprobante = await _context.Comprobantes
                .Include(c => c.Detalles)
                .FirstOrDefaultAsync(c => c.Id == id);

            if (comprobante == null)
                return NotFound();

            return View(comprobante);
        }
    }
}

