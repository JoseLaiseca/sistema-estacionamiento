# Resumen de Arquitectura ARCA - Web Services SOAP

Basado en: https://www.arca.gob.ar/ws/documentacion/arquitectura-general.asp

## 🔑 Conceptos Clave

### 1. WSAA (Web Service de Autenticación y Autorización)

**Función:**
- Autentica aplicaciones clientes
- Otorga permisos de acceso a cada WSN mediante un **Ticket de Acceso (TA)**

**Características del TA:**
- ✅ Válido para **un WSN específico** (ej: `ws_sr_padron_a5`, `wsfe`)
- ✅ Validez limitada: **12 horas**
- ✅ La aplicación cliente debe presentar el TA al WSN, de lo contrario será rechazada

### 2. Autenticación con Certificados X.509

**Proceso:**
1. ARCA actúa como Autoridad Certificante
2. Emite certificados digitales X.509 **sin cargo**
3. El cliente solicita un TA enviando:
   - Estructura CMS (PKCS#7, S/MIME)
   - Solicitud (TRA - Ticket de Requerimiento de Acceso)
   - Firma digital
   - Certificado X.509

**Validación del WSAA:**
- ✅ Verifica la firma digital
- ✅ Comprueba en su base de datos que el EE haya completado los trámites de autorización inicial
- ✅ Verifica que el servicio esté autorizado para ese certificado

### 3. Token y Sign

**Extracción del TA:**
- El cliente extrae del TA dos componentes:
  - **Token**: Identificador del ticket
  - **Sign**: Firma del ticket

**Uso en WSN:**
- En cada solicitud al WSN, el cliente envía:
  - Token
  - Sign
  - Datos de negocio

## 📋 Flujo Completo

```
1. Cliente → WSAA: Solicita TA (con certificado X.509 firmado)
2. WSAA → Cliente: Devuelve TA (con Token y Sign)
3. Cliente → WSN: Envía Token, Sign + Datos de negocio
4. WSN → Cliente: Devuelve respuesta
```

## ✅ Verificación de Implementación Actual

### Tu código cumple con:

1. ✅ **Solicitud de TA al WSAA**
   - Método: `ObtenerTokenAsync()`
   - Firma con certificado: `FirmarMensaje()`
   - Envío: `EnviarMensajeWSAAAsync()`

2. ✅ **Extracción de Token y Sign**
   - Método: `ProcesarRespuestaWSAA()`
   - Extrae `token` y `sign` del TA

3. ✅ **Uso en WSN**
   - Método: `CrearXMLConsultaWSP()`
   - Incluye `token` y `sign` en el XML

4. ✅ **Validez de 12 horas**
   - Verificación: `ExpirationTime > DateTime.Now.AddMinutes(5)`
   - Caché en memoria y disco

5. ✅ **TA específico por servicio**
   - Clave de caché: `{CUIT}_{Entorno}_{Servicio}`
   - Diferentes TAs para `wsfe` y `ws_sr_padron_a5`

## 🚨 Puntos Críticos

### 1. Orden de Elementos en XML (CRÍTICO)

Según la documentación y tu implementación, el orden debe ser:

```xml
<a5:getPersona_v2>
   <token>...</token>              <!-- 1. Token del TA -->
   <sign>...</sign>                <!-- 2. Sign del TA -->
   <cuitRepresentada>...</cuitRepresentada>  <!-- 3. CUIT del certificado -->
   <idPersona>...</idPersona>       <!-- 4. CUIT a consultar -->
</a5:getPersona_v2>
```

**✅ Tu código ya implementa esto correctamente**

### 2. Autorización del Servicio

**Requisito ARCA:**
- El EE debe completar trámites de autorización inicial
- El servicio debe estar autorizado en el portal de AFIP

**Verificación:**
- Si recibes "No existe persona con ese Id", puede ser:
  1. El servicio `ws_sr_padron_a5` no está autorizado
  2. El CUIT consultado no existe en el padrón del entorno

### 3. Certificado X.509

**Requisitos:**
- ✅ Certificado válido y no expirado
- ✅ CUIT del certificado coincide con `CuitServicioAFIP`
- ✅ Certificado tiene permisos para el servicio solicitado

**Tu código verifica:**
- ✅ Validez del certificado: `VerificarCertificadoAsync()`
- ✅ Extracción del CUIT: `ExtraerCUITDelCertificado()`
- ✅ Validación de coincidencia: Compara con `_cuitServicio`

## 📚 Referencias

- [Documentación ARCA - Arquitectura General](https://www.arca.gob.ar/ws/documentacion/arquitectura-general.asp)
- [Manual WSAA](https://www.afip.gob.ar/fe/documentos/manual_desarrollador_COMPG_v2_10.pdf)

## ✅ Conclusión

Tu implementación **cumple correctamente** con la arquitectura definida por ARCA:

1. ✅ Solicita TA al WSAA con certificado firmado
2. ✅ Extrae Token y Sign del TA
3. ✅ Usa Token y Sign en solicitudes al WSN
4. ✅ Respeta la validez de 12 horas
5. ✅ Maneja TAs específicos por servicio
6. ✅ Implementa caché para evitar solicitudes innecesarias

**El problema actual ("No existe persona con ese Id") NO es un problema de arquitectura, sino probablemente:**
- Servicio no autorizado en el portal de AFIP
- CUIT no disponible en el padrón de Homologación










