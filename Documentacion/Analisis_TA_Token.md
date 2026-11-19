# Análisis del Token de Acceso (TA) - WSAA

## 📋 Información del TA Recibido

### Datos del Header
- **Source (Origen):** CN=wsaahomo, O=AFIP, C=AR, SERIALNUMBER=CUIT 33693450239
- **Destination (Destino):** SERIALNUMBER=CUIT CUIT_AQUI, CN=appplaya2025
- **Unique ID:** 1434568431
- **Generation Time:** 2025-11-12T00:10:08.105-03:00
- **Expiration Time:** 2025-11-12T12:10:08.105-03:00
- **Validez:** 12 horas (correcto según documentación ARCA)

### Token Decodificado (Base64)

El token contiene la siguiente información XML:

```xml
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<sso version="2.0">
    <id src="CN=wsaahomo, O=AFIP, C=AR, SERIALNUMBER=CUIT 33693450239" 
        unique_id="216150768" 
        gen_time="1762916948" 
        exp_time="1762960208"/>
    <operation type="login" value="granted">
        <login entity="33693450239" 
               service="ws_sr_padron_a5" 
               uid="SERIALNUMBER=CUIT CUIT_AQUI, CN=appplaya2025" 
               authmethod="cms" 
               regmethod="22">
            <relations>
                <relation key="CUIT_AQUI" reltype="4"/>
            </relations>
        </login>
    </operation>
</sso>
```

## ✅ Análisis Crítico

### 1. Servicio Autorizado ✅

**IMPORTANTE:** El token muestra claramente:
- `service="ws_sr_padron_a5"` ✅
- `value="granted"` ✅ (operación concedida)
- `entity="33693450239"` (entidad de AFIP)
- `uid="SERIALNUMBER=CUIT CUIT_AQUI, CN=appplaya2025"` (tu certificado)

**Conclusión:** El servicio `ws_sr_padron_a5` **SÍ está autorizado** para tu certificado.

### 2. Relación del CUIT ✅

El token incluye:
```xml
<relation key="CUIT_AQUI" reltype="4"/>
```

Esto confirma que el CUIT `CUIT_AQUI` está relacionado con el certificado.

### 3. Validez del Token ✅

- Generado: 2025-11-12T00:10:08
- Expira: 2025-11-12T12:10:08
- Duración: 12 horas (correcto)

## 🔍 ¿Por qué entonces el error "No existe persona con ese Id"?

Si el servicio **SÍ está autorizado** (como muestra el token), entonces el problema es:

### Posibilidad 1: CUIT no existe en Homologación (MÁS PROBABLE)

El entorno de **Homologación tiene un padrón limitado**. No todos los CUITs reales existen en Homologación.

**Solución:**
- Probar con CUITs de prueba conocidos en Homologación
- O cambiar a Producción (si tienes certificado de producción)

### Posibilidad 2: Problema con el XML del Request

Aunque el token está autorizado, el XML del request podría tener algún problema.

**Verificar:**
- Orden de elementos: token → sign → cuitRepresentada → idPersona
- Namespace correcto: `xmlns:a5="http://a5.soap.ws.server.puc.sr/"`
- Método correcto: `getPersona_v2` (no `getPersona`)

### Posibilidad 3: Problema temporal de AFIP

El servicio podría estar teniendo problemas temporales.

**Solución:**
- Esperar unos minutos y volver a intentar
- Verificar el estado de los servicios de AFIP

## 📝 Recomendaciones

### 1. Verificar el XML del Request

Revisar el archivo `Certificados/WSP5_Request.xml` y verificar:
- ✅ Orden correcto de elementos
- ✅ Namespace correcto
- ✅ `cuitRepresentada` = `CUIT_AQUI` (tu CUIT)
- ✅ `idPersona` = CUIT a consultar

### 2. Probar con CUITs de Prueba

En Homologación, probar con CUITs que se sabe que existen:
- Tu propio CUIT: `CUIT_AQUI` (debería funcionar si está en el padrón)
- CUITs de prueba comunes en Homologación

### 3. Revisar la Respuesta Completa

Revisar `Certificados/WSP5_Response.xml` para ver el error exacto que devuelve AFIP.

## ✅ Conclusión

**El servicio `ws_sr_padron_a5` ESTÁ AUTORIZADO** según el token recibido.

El problema es probablemente:
1. El CUIT consultado no existe en el padrón de Homologación
2. Algún problema menor en el formato del XML del request
3. Problema temporal del servicio de AFIP

**Próximos pasos:**
1. Verificar el XML del request (`WSP5_Request.xml`)
2. Probar con tu propio CUIT primero
3. Si falla, probar con otros CUITs
4. Revisar la respuesta completa de AFIP










