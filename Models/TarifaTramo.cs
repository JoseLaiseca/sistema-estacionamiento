namespace EstacionamientoWebApp.Models
{
    public class TarifaTramo
    {
        public int Id { get; set; }
        public int MinutosDesde { get; set; }   // inclusive
        public int MinutosHasta { get; set; }   // inclusive
        public decimal Importe { get; set; }    // precio fijo a cobrar en el tramo
        public int Prioridad { get; set; }      // por si se solapan, menor = primero
        public bool Activo { get; set; } = true;
    }
}


