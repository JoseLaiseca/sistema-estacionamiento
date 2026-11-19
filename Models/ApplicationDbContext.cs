using Microsoft.EntityFrameworkCore;

namespace EstacionamientoWebApp.Models
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options) { }

        public DbSet<IngresoModel> Ingresos { get; set; }
        public DbSet<EstadiaModel> Estadias { get; set; }
        public DbSet<TarifaTramo> TarifaTramos { get; set; }
        public DbSet<Mensual> Mensuales { get; set; }
        public DbSet<Comprobante> Comprobantes { get; set; }
        public DbSet<DetalleComprobante> DetalleComprobantes { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // --------- Estadia ----------
            modelBuilder.Entity<EstadiaModel>()
                .Property(e => e.Monto)
                .HasColumnType("decimal(12,2)");

            // --------- Mensual ----------
            modelBuilder.Entity<Mensual>(entity =>
            {
                entity.ToTable("Mensuales");

                entity.Property(m => m.Precio)
                      .HasColumnType("decimal(12,2)");

                // Defaults en DB (evitan NULLs si el código no manda el valor)
                entity.Property(m => m.DejoLlave)
                      .HasDefaultValue(false);

                entity.Property(m => m.Activo)
                      .HasDefaultValue(true);

                entity.Property(m => m.Eliminado)
                      .HasDefaultValue(false);

                // Si querés defaults de fecha del lado de SQL
                entity.Property(m => m.Fecha)
                      .HasDefaultValueSql("GETDATE()");

                entity.Property(m => m.FechaVencimiento)
                      .HasDefaultValueSql("DATEADD(month, 1, GETDATE())");
            });

            // --------- TarifaTramo ----------
            modelBuilder.Entity<TarifaTramo>()
                .Property(t => t.Importe)
                .HasColumnType("decimal(12,2)");

            // Seed de ejemplo (ajustá si ya tenés datos)
            modelBuilder.Entity<TarifaTramo>().HasData(
                new TarifaTramo { Id = 1, MinutosDesde = 0, MinutosHasta = 65, Importe = 1500m, Prioridad = 1, Activo = true },
                new TarifaTramo { Id = 2, MinutosDesde = 66, MinutosHasta = 75, Importe = 2000m, Prioridad = 1, Activo = true },
                new TarifaTramo { Id = 3, MinutosDesde = 76, MinutosHasta = 120, Importe = 800m, Prioridad = 1, Activo = true }
            );

            // --------- Comprobante ----------
            modelBuilder.Entity<Comprobante>(entity =>
            {
                entity.ToTable("Comprobantes");
                entity.HasKey(c => c.Id);
                entity.HasIndex(c => new { c.PuntoVenta, c.TipoComprobante, c.NumeroComprobante });
                
                entity.Property(c => c.FechaEmision)
                      .HasDefaultValueSql("GETDATE()");
                
                entity.Property(c => c.FechaCreacion)
                      .HasDefaultValueSql("GETDATE()");
            });

            // --------- DetalleComprobante ----------
            modelBuilder.Entity<DetalleComprobante>(entity =>
            {
                entity.ToTable("DetalleComprobantes");
                entity.HasKey(d => d.Id);
                
                entity.HasOne(d => d.Comprobante)
                      .WithMany(c => c.Detalles)
                      .HasForeignKey(d => d.ComprobanteId)
                      .OnDelete(DeleteBehavior.Cascade);
            });
        }
    }
}

