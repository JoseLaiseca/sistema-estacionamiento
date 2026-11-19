using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstacionamientoWebApp.Migrations
{
    /// <inheritdoc />
    public partial class AddCamposClienteFE : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NumeroDoc",
                table: "Comprobantes",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "TipoDoc",
                table: "Comprobantes",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NumeroDoc",
                table: "Comprobantes");

            migrationBuilder.DropColumn(
                name: "TipoDoc",
                table: "Comprobantes");
        }
    }
}
