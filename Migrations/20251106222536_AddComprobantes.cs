using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstacionamientoWebApp.Migrations
{
    /// <inheritdoc />
    public partial class AddComprobantes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Comprobantes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PuntoVenta = table.Column<int>(type: "int", nullable: false),
                    TipoComprobante = table.Column<int>(type: "int", nullable: false),
                    NumeroComprobante = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FechaEmision = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    CuitCliente = table.Column<string>(type: "nvarchar(11)", maxLength: 11, nullable: true),
                    RazonSocialCliente = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DomicilioCliente = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CondicionVenta = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Concepto = table.Column<string>(type: "nvarchar(2)", maxLength: 2, nullable: true),
                    ImporteTotal = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    ImporteNeto = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    ImporteIVA = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    CAE = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    FechaVtoCAE = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Estado = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Observaciones = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FechaCreacion = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETDATE()"),
                    FechaActualizacion = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comprobantes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DetalleComprobantes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ComprobanteId = table.Column<int>(type: "int", nullable: false),
                    Descripcion = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Cantidad = table.Column<decimal>(type: "decimal(10,2)", nullable: false),
                    PrecioUnitario = table.Column<decimal>(type: "decimal(12,2)", nullable: false),
                    AlicuotaIVA = table.Column<decimal>(type: "decimal(5,2)", nullable: false),
                    CodigoProducto = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    UnidadMedida = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DetalleComprobantes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DetalleComprobantes_Comprobantes_ComprobanteId",
                        column: x => x.ComprobanteId,
                        principalTable: "Comprobantes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Comprobantes_PuntoVenta_TipoComprobante_NumeroComprobante",
                table: "Comprobantes",
                columns: new[] { "PuntoVenta", "TipoComprobante", "NumeroComprobante" });

            migrationBuilder.CreateIndex(
                name: "IX_DetalleComprobantes_ComprobanteId",
                table: "DetalleComprobantes",
                column: "ComprobanteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DetalleComprobantes");

            migrationBuilder.DropTable(
                name: "Comprobantes");
        }
    }
}
