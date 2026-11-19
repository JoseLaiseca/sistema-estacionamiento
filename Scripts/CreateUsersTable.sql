-- ============================================
-- Script para crear la tabla Users y usuario de prueba
-- ============================================
-- Ejecutar en SQL Server Management Studio
-- Base de datos: appPlayaLocal

USE [appPlayaLocal];
GO

PRINT '=========================================';
PRINT 'CREACIÓN DE TABLA Users';
PRINT '=========================================';
PRINT '';

-- Crear tabla Users si no existe
IF NOT EXISTS (SELECT * FROM sys.objects WHERE object_id = OBJECT_ID(N'[dbo].[Users]') AND type in (N'U'))
BEGIN
    CREATE TABLE [dbo].[Users] (
        [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        [Username] NVARCHAR(100) NOT NULL UNIQUE,
        [PasswordHash] NVARCHAR(500) NOT NULL,
        [DisplayName] NVARCHAR(200) NULL,
        [Playa] NVARCHAR(200) NULL,
        [ConnectionString] NVARCHAR(500) NULL,
        [FechaCreacion] DATETIME2 NOT NULL DEFAULT GETDATE(),
        [FechaActualizacion] DATETIME2 NULL
    );

    -- Crear índice en Username para búsquedas rápidas
    CREATE INDEX [IX_Users_Username] ON [dbo].[Users] ([Username]);

    PRINT '✓ Tabla Users creada exitosamente.';
    PRINT '';
END
ELSE
BEGIN
    PRINT '✓ La tabla Users ya existe.';
    PRINT '';
END

-- El primer usuario se crea a mano: generar el hash con AuthService.HashPassword("<clave>") y ejecutar
-- INSERT INTO [dbo].[Users] ([Username], [PasswordHash], [DisplayName], [Playa], [ConnectionString])
-- VALUES ('admin', '<hash BCrypt>', 'Administrador', 'Playa Principal', '<cadena de conexion>');

-- Verificar usuarios creados
PRINT '=========================================';
PRINT 'USUARIOS REGISTRADOS:';
PRINT '=========================================';
SELECT 
    [Id],
    [Username],
    [DisplayName],
    [Playa],
    [FechaCreacion]
FROM [dbo].[Users]
ORDER BY [Id];
GO
