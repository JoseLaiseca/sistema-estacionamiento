# Ejemplos de Referencia - AFIP WS_SR_PADRON_A5

Este directorio contiene ejemplos de referencia para implementar correctamente el servicio WS_SR_PADRON_A5 de AFIP.

## 📋 Estructura del Proyecto

```
Ejemplos/
├── EjemploAFIPPadronA5.cs      # Clase de ejemplo completa
└── README_EJEMPLOS.md          # Este archivo
```

## 🔑 Puntos Críticos de la Implementación

### 1. Orden de Elementos en el XML (CRÍTICO)

El orden de los elementos dentro de `<a5:getPersona_v2>` es **OBLIGATORIO**:

```xml
<a5:getPersona_v2>
   <token>...</token>              <!-- 1. Token -->
   <sign>...</sign>                <!-- 2. Sign -->
   <cuitRepresentada>...</cuitRepresentada>  <!-- 3. CUIT del certificado -->
   <idPersona>...</idPersona>      <!-- 4. CUIT a consultar -->
</a5:getPersona_v2>
```

**⚠️ Si cambias el orden, AFIP devolverá error "Unmarshalling Error"**

### 2. Namespace Correcto

```xml
xmlns:a5="http://a5.soap.ws.server.puc.sr/"
```

### 3. CUIT Representada vs CUIT Consultado

- **cuitRepresentada**: DEBE ser el CUIT del certificado (el que está en `appsettings.json` como `CuitServicioAFIP`)
- **idPersona**: Es el CUIT que quieres consultar

**Ejemplo:**
- Certificado: `CUIT_AQUI`
- Consultar: `20365039829`

```xml
<cuitRepresentada>CUIT_AQUI</cuitRepresentada>
<idPersona>20365039829</idPersona>
```

## 📝 Ejemplo de Uso

```csharp
// 1. Obtener token (desde tu AFIPService)
var token = await afipService.ObtenerTokenAsync("ws_sr_padron_a5");

// 2. Crear instancia del ejemplo
var ejemplo = new EjemploAFIPPadronA5(
    urlWSP5: "https://awshomo.afip.gov.ar/sr-padron/webservices/personaServiceA5",
    cuitServicio: "CUIT_AQUI"
);

// 3. Consultar persona
try
{
    var resultado = await ejemplo.ConsultarPersonaAsync(
        cuitConsultado: "20365039829",
        token: token.Token,
        sign: token.Sign
    );
    
    Console.WriteLine($"Razón Social: {resultado.RazonSocial}");
    Console.WriteLine($"Estado: {resultado.Estado}");
    Console.WriteLine($"Condición IVA: {resultado.CondicionIVA}");
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
```

## 🚨 Errores Comunes

### Error: "No existe persona con ese Id"

**Causas posibles:**
1. El CUIT consultado no existe en el padrón del entorno (Homologación tiene padrón limitado)
2. El certificado no tiene autorizado el servicio `ws_sr_padron_a5`
3. El `cuitRepresentada` no coincide con el CUIT del certificado

**Solución:**
- Verificar en el portal de AFIP que el servicio esté autorizado
- Probar con el CUIT del certificado primero
- Verificar que `CuitServicioAFIP` en `appsettings.json` coincida con el CUIT del certificado

### Error: "Unmarshalling Error: unexpected element"

**Causa:** Orden incorrecto de elementos en el XML

**Solución:** Verificar que el orden sea: token → sign → cuitRepresentada → idPersona

## 📚 Referencias

- [Documentación Oficial AFIP](https://www.afip.gob.ar/fe/documentos/manual_desarrollador_COMPG_v2_10.pdf)
- WSDL Homologación: https://awshomo.afip.gov.ar/sr-padron/webservices/personaServiceA5?wsdl
- WSDL Producción: https://aws.afip.gov.ar/sr-padron/webservices/personaServiceA5?wsdl

## 🔍 Cómo Usar Estos Ejemplos en Tu Proyecto

1. **Revisar el código de ejemplo** en `EjemploAFIPPadronA5.cs`
2. **Comparar con tu implementación actual** en `Services/AFIPService.cs`
3. **Aplicar las mejoras necesarias** basándote en las diferencias encontradas

## ✅ Checklist de Implementación

- [ ] Orden correcto de elementos en XML
- [ ] Namespace correcto (`xmlns:a5`)
- [ ] `cuitRepresentada` = CUIT del certificado
- [ ] `idPersona` = CUIT a consultar
- [ ] Manejo correcto de errores SOAP
- [ ] Procesamiento correcto de respuesta exitosa
- [ ] Validación de CUIT antes de enviar
- [ ] Logging detallado para debugging










