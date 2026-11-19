using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstacionamientoWebApp.Migrations
{
    /// <inheritdoc />
    public partial class AddTipoVehiculoToEstadia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TipoVehiculo",
                table: "Estadias",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TipoVehiculo",
                table: "Estadias");
        }
    }
}
