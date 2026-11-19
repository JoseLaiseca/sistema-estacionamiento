using EstacionamientoWebApp.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace EstacionamientoWebApp.Controllers
{
    public class MensualesController : Controller
    {
        private readonly ApplicationDbContext _db;

        public MensualesController(ApplicationDbContext db)
        {
            _db = db;
        }

        // Formulario + lista
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            ViewBag.Nuevo = new Mensual
            {
                Activo = true,
                FechaInicio = DateTime.Today
            };

            var lista = await _db.Mensuales
                                 .OrderByDescending(m => m.Id)
                                 .ToListAsync();

            return View(lista); // la vista recibe IEnumerable<Mensual> y usa ViewBag.Nuevo para el form
        }

        // Guarda y vuelve al Index
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Crear(Mensual nuevo)
        {
            if (!ModelState.IsValid)
            {
                ViewBag.Nuevo = nuevo;
                var lista = await _db.Mensuales
                                     .OrderByDescending(m => m.Id)
                                     .ToListAsync();
                return View("Index", lista);
            }

            nuevo.Patente = (nuevo.Patente ?? string.Empty).Trim().ToUpperInvariant();

            _db.Mensuales.Add(nuevo);
            await _db.SaveChangesAsync();

            TempData["ok"] = "Mensual guardado correctamente.";
            return RedirectToAction(nameof(Index));
        }
    }
}
