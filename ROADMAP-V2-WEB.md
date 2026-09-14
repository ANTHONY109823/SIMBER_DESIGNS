# SIMBER DESIGNS — V2 (web) · Roadmap para continuar en Cursor

Modelo de negocio (definido 2026-09-14): **la V2 es descargable pero REQUIERE internet**
y valida siempre contra Postgres (estilo Adobe). Suscripción **desde US$15/mes por PC**.
La **IA se cobra por créditos** del mismo saldo del usuario.

> Regla de oro: **no toques lo que ya funciona** (firma de licencias, HWID, llaves,
> formato `SIMBER.<payload>.<firma>`, activación del plugin). La **V1 no lleva IA** y se
> distribuye aparte — no la modifiques. Ver `../PLUGIN ARMADO EN COREL/SimberDesigns/CLAUDE.md`.

---

## ✅ Ya hecho (backend, desplegado en Railway)

- **IA por créditos** — `POST /api/ia/leer-lista` (`SimberDesigns.Server/Controllers/IaController.cs`):
  el plugin manda foto + licencia PREMIUM firmada (`X-Simber-License` / `X-Simber-Hwid`);
  el servidor verifica la firma, exige `IncluyeIa=true`, ubica al usuario por el HWID de su
  `PluginLicense`, **descuenta `Anthropic__CreditsPerRead` créditos** (def. 1) y registra
  `CreditTransaction` (`IA_Read`). Sin saldo → 402. La clave de Anthropic vive SOLO en el
  servidor (`Anthropic__ApiKey`), nunca en el `.exe`.
- **Acreditación de créditos arreglada** — antes, pagar un paquete NO sumaba créditos.
  Ahora sí: `PaymentFulfillmentService.AcreditarCreditosAsync` (flujo MercadoPago) y
  `PaymentsController.ReviewAsync` (aprobación manual del admin, con guarda anti doble-cobro).
- **MercadoPago automático** — `MercadoPagoService` está COMPLETO (API real). Se queda en
  modo prueba hasta poner credenciales reales (abajo).
- **Precio suscripción** — `MercadoPago:PluginMonthPricePen = 56` (≈ US$15/mes).

## 🔧 Config a poner en Railway (variables) — las pone Anthony, no van al repo

    Anthropic__ApiKey            = sk-ant-...        (tras cargar créditos en console.anthropic.com)
    Anthropic__Model             = claude-sonnet-5   (o claude-haiku-4-5 para abaratar)
    Anthropic__CreditsPerRead    = 1
    MercadoPago__UseFakeCheckout = false
    MercadoPago__AccessToken     = APP_USR-...       (token real de MercadoPago)
    MercadoPago__PublicBaseUrl   = https://simberdesigns-production.up.railway.app
    MercadoPago__PluginMonthPricePen = 56            (ajustar al tipo de cambio / precio real)

Ya existentes (no borrar): `Licensing__PrivateKey` (firma licencias), conexión Postgres.

---

## 🚧 Pendiente para Cursor (la parte WEB / UI)

1. **Pantalla de Recarga** (`/recargar`): mostrar la **suscripción $15/mes** (botón "Pagar el mes"
   → `POST /api/payments/checkout` con `kind:"plugin"`) y los **paquetes de créditos** para IA
   (`GET /api/payments/storefront`). Al volver de MercadoPago, `/pago/ok` debe llamar
   `POST /api/payments/confirm` con el `paymentId`.
2. **Panel del usuario** (`/cuenta`): mostrar **saldo de créditos** (`GET /api/account/...`),
   estado de la **licencia del plugin** (`GET /api/plugin/me`: activa/vence) e historial
   (`GET /api/payments/mine`). Botón para recargar créditos y renovar el mes.
3. **Definir precios finales**: cuántos créditos trae cada paquete y su precio (hoy 160/400/800),
   y cuántos créditos cuesta una lectura IA (hoy 1). Cuadrar el margen contra el costo real de Claude.
4. **Probar el flujo real de MercadoPago** con una compra chica (tras poner credenciales):
   checkout → pago → webhook (`/api/webhooks/mercadopago`) → créditos acreditados → saldo sube.
5. **Admin** (`/admin`): revisar que las métricas y el historial de ventas/licencias muestren
   las nuevas transacciones de créditos e IA.

## 🖥️ Desktop V2 (opcional, cuando se publique el `.exe`)

- La V2 ya valida online al activar y renueva el token contra Postgres cuando vence
  (`App.xaml.cs` → `TrySilentRenew`, `WebLicenseClient`). El token dura 30 días = "gracia" offline.
- **Mejora Adobe-like (opcional, NO hecha para no romper lo que funciona):** revalidar online
  en CADA arranque en segundo plano (fire-and-forget) para reflejar de inmediato una baja/cancelación,
  manteniendo la gracia offline. Si se hace, que NO bloquee el arranque por un corte breve de internet.
- El lector de IA del `.exe` (`ClaudeVisionReader`) ya manda la licencia al endpoint. **Al publicar
  la V2, recompilar** con `SimberDesigns/build-v2-seguro.ps1` y subir el `.exe` a GitHub Releases
  (`Plugin__DownloadUrl`). El `.bas`/`.jsx` NO se envían.

## ⛔ No tocar
Firma/verificación de licencias (`Licensing/`), HWID, `AdminKeys`/`SimberKeys`, el `.exe` de V1,
los motores de armado (`mSimberArmado.bas` / `simberArmado.jsx`). La clave de Anthropic **jamás**
en el `.exe`: siempre server-side.
