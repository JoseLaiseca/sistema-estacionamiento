using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EstacionamientoWebApp.Migrations
{
    /// <inheritdoc />
    public partial class AddMensuales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FechaFin",
                table: "Mensuales",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaInicio",
                table: "Mensuales",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Observaciones",
                table: "Mensuales",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PrecioMensual",
                table: "Mensuales",
                type: "decimal(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Telefono",
                table: "Mensuales",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FechaFin",
                table: "Mensuales");

            migrationBuilder.DropColumn(
                name: "FechaInicio",
                table: "Mensuales");

            migrationBuilder.DropColumn(
                name: "Observaciones",
                table: "Mensuales");

            migrationBuilder.DropColumn(
                name: "PrecioMensual",
                table: "Mensuales");

            migrationBuilder.DropColumn(
                name: "Telefono",
                table: "Mensuales");
        }
    }
}
