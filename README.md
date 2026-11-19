# Sistema de estacionamiento

Aplicación web para administrar una playa de estacionamiento: ingresos y egresos de vehículos, cobro por tramos de tarifa, abonos mensuales y **facturación electrónica con ARCA (ex AFIP)**. Está en uso real en una playa de La Rioja, Argentina.

> Este repositorio es una versión sin datos ni configuración del negocio: nombres, CUIT y direcciones de los tickets son de ejemplo.

## Qué hace

- **Ingresos y egresos** de vehículos, con cálculo del cobro según tramos de tarifa configurables.
- **Tickets impresos** en impresora térmica y comprobantes en PDF con código QR.
- **Abonos mensuales** (clientes con cuota).
- **Facturación electrónica ARCA/AFIP:** autenticación con WSAA, emisión de comprobantes con WSFE, CAE y QR fiscal, y consulta de datos del cliente por CUIT (padrón A5).
- **Multi-playa:** cada usuario trabaja contra la base de datos de su playa.
- **Login** con contraseñas hasheadas con BCrypt.

## Stack

- C# y ASP.NET Core 8 MVC (Razor)
- Entity Framework Core y Dapper sobre SQL Server
- QuestPDF, QRCoder y ZXing para PDF y códigos
- Integración SOAP con los web services de ARCA (WSAA, WSFE, Padrón A5)
- Bootstrap y JavaScript en el front

## Dónde mirar

| Carpeta | Contenido |
|---|---|
| `Controllers/` | Ingresos, mensuales, facturación y configuración de ARCA |
| `Services/WSAA`, `Services/WSFE` | Clientes de los servicios de ARCA |
| `Services/TarifasService.cs` | Cálculo de cobro por tramos |
| `Services/AuthService.cs` | Autenticación con BCrypt |
| `Documentacion/` | Notas sobre la arquitectura de ARCA y la autorización de servicios |

## Cómo correrlo

1. Tener .NET 8 SDK y SQL Server (sirve LocalDB).
2. Copiar `appsettings.example.json` sobre `appsettings.json` y completar las conexiones.
3. Crear la tabla de usuarios con `Scripts/CreateUsersTable.sql` y cargar un usuario con un hash generado por `AuthService.HashPassword`.
4. Aplicar las migraciones: `dotnet ef database update`.
5. `dotnet run`.

Para facturar hace falta un certificado de ARCA (homologación o producción), que no se incluye en el repositorio. Ver `Documentacion/Guia_Autorizar_Servicio_AFIP.md`.

## Autor

José Laiseca, desarrollador fullstack. [LinkedIn](https://www.linkedin.com/in/JoseLaiseca)
