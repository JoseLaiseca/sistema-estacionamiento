using System;
using System.Collections.Generic;

namespace EstacionamientoWebApp.Models
{
    public class IngresoViewModel
    {
        public IngresoModel Ingreso { get; set; } = new IngresoModel();
        public List<IngresoModel> Ingresos { get; set; } = new List<IngresoModel>();
        public DateTime? FechaFiltro { get; set; }
        public EstadiaModel? Estadia { get; set; }
        public Mensual? Mensual { get; set; }
        public List<Mensual> Mensuales { get; set; } = new List<Mensual>();
    }
}
