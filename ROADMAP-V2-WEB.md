# SIMBER DESIGNS — V2 (web) · Roadmap para continuar en Cursor

Modelo de negocio (definido 2026-09-14): **la V2 es descargable pero REQUIERE internet**
y valida siempre contra Postgres (estilo Adobe). Suscripción **desde US$15/mes por PC**.
Con la suscripción activa la **IA es LIBRE** (lecturas ilimitadas, no consume créditos).
Los **créditos son solo para DESCARGAR** los diseños/CDR del catálogo de la web.

> Regla de oro: **no toques lo que ya funciona** (firma de licencias, HWID, llaves,
> formato `SIMBER.<payload>.<firma>`, activación del plugin). La **V1 no lleva IA** y se
> distribuye aparte — no la modifiques. Ver `../PLUGIN ARMADO EN COREL/SimberDesigns/CLAUDE.md`.

---

## ✅ Ya hecho (backend, desplegado en Railway)

- **IA LIBRE con suscripción** — `POST /api/ia/leer-lista` (`SimberDesigns.Server/Controllers/IaController.cs`):
  el plugin manda foto + licencia PREMIUM firmada (`X-Simber-License` / `X-Simber-Hwid`);
  el servidor verifica la firma y exige que la licencia esté **vigente** e `IncluyeIa=true`.
  Como el token solo se renueva mientras la suscripción de $15/mes está pagada, licencia vigente =
  suscripción activa → **IA ilimitada, sin cobro por consulta**. La clave de Anthropic vive SOLO en el
  servidor (`Anthropic__ApiKey`), nunca en el `.exe`.
- **Créditos (solo descargas) arreglados** — antes, pagar un paquete NO sumaba créditos.
  Ahora sí: `PaymentFulfillmentService.AcreditarCreditosAsync` (flujo MercadoPago) y
  `PaymentsController.ReviewAsync` (aprobación manual del admin, con guarda anti doble-cobro).
  Esos créditos se gastan al **descargar diseños** del catálogo (`DesignsController`), NO en la IA.
- **MercadoPago automático** — `MercadoPagoService` está COMPLETO (API real). Se queda en
  modo prueba hasta poner credenciales reales (abajo).
- **Precio suscripción** — `MercadoPago:PluginMonthPricePen = 56` (≈ US$15/mes).

## 🔧 Config a poner en Railway (variables) — las pone Anthony, no van al repo

    Anthropic__ApiKey            = sk-ant-...        (tras cargar saldo en console.anthropic.com)
    Anthropic__Model             = claude-sonnet-5   (o claude-haiku-4-5 para abaratar)
    MercadoPago__UseFakeCheckout = false
    MercadoPago__AccessToken     = APP_USR-...       (token real de MercadoPago)
    MercadoPago__PublicBaseUrl   = https://simberdesigns-production.up.railway.app
    MercadoPago__PluginMonthPricePen = 56            (ajustar al tipo de cambio / precio real)

Ya existentes (no borrar): `Licensing__PrivateKey` (firma licencias), conexión Postgres.

---

## 🚧 Pendiente para Cursor (la parte WEB / UI)

1. **Pantalla de Recarga** (`/recargar`): mostrar la **suscripción $15/mes** (botón "Pagar el mes"
   → `POST /api/payments/checkout` con `kind:"plugin"`; incluye IA libre) y los **paquetes de créditos
   para DESCARGAR diseños** (`GET /api/payments/storefront`). Al volver de MercadoPago, `/pago/ok`
   debe llamar `POST /api/payments/confirm` con el `paymentId`.
2. **Panel del usuario** (`/cuenta`): mostrar **saldo de créditos** (`GET /api/account/...`),
   estado de la **licencia del plugin** (`GET /api/plugin/me`: activa/vence) e historial
   (`GET /api/payments/mine`). Botón para recargar créditos y renovar el mes.
3. **Definir precios finales**: el mes ($15, IA libre) y cuántos créditos trae cada paquete de
   DESCARGAS y su precio (hoy 160/400/800), más el costo en créditos por diseño (`Design.CreditsCost`).
4. **Probar el flujo real de MercadoPago** con una compra chica (tras poner credenciales):
   checkout → pago → webhook (`/api/webhooks/mercadopago`) → créditos acreditados → saldo sube.
5. **Admin** (`/admin`): revisar que las métricas y el historial de ventas/licencias muestren
   las nuevas transacciones de créditos e IA.

## 🖥️ Desktop V2 — validación online en cada apertura ✅ HECHO (falta publicar el `.exe`)

- **Implementado (2026-09-14):** al abrir la V2, `App.xaml.cs` valida ONLINE contra Postgres en CADA
  apertura vía `WebLicenseClient.RevalidateAsync` → `POST /api/plugin/revalidate` (manda el token firmado
  ligado al HWID; NO usa JWT, así no pide email/clave cada vez). Resultados:
  - **Ok** → re-emite token fresco y abre. **402 (mes vencido)** → mensaje "renueva el mes" + activación.
  - **Sin internet** → gracia MUY corta (`GraciaDias=2`, marca `.sdonline` en `%LOCALAPPDATA%\SimberDesigns`):
    si validó hace ≤2 días y el token sigue vigente, abre; si no, mensaje "necesitas internet" y cierra.
  - Primera vez (sin licencia) → ventana de activación con login. La V1 NO cambia (activación offline).
- **PENDIENTE:** al publicar la V2, **recompilar** con `SimberDesigns/build-v2-seguro.ps1` y subir el
  `.exe` a GitHub Releases (`Plugin__DownloadUrl`). El `.bas`/`.jsx` NO se envían.
- Posible ajuste futuro: `GraciaDias` (0 = internet obligatorio siempre, sin gracia).

## ⛔ No tocar
Firma/verificación de licencias (`Licensing/`), HWID, `AdminKeys`/`SimberKeys`, el `.exe` de V1,
los motores de armado (`mSimberArmado.bas` / `simberArmado.jsx`). La clave de Anthropic **jamás**
en el `.exe`: siempre server-side.
