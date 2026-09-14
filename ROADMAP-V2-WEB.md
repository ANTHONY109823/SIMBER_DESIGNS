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

## ✅ Web / UI — HECHO

1. **`/recargar`** (`Pricing.razor`): hero de **suscripción $15/mes con IA ilimitada incluida**
   ("Pagar el mes" → checkout `kind:"plugin"`) + **paquetes de créditos para DESCARGAR diseños**.
2. **`/pago/ok|error|pendiente`** (`PaymentResult.razor`): confirma el pago (`ConfirmPaymentAsync`).
3. **`/cuenta`** (`Account.razor`): saldo de créditos (descargas), estado de la suscripción
   (activa/vence + "IA ilimitada incluida" + código de activación) e **historial** de movimientos
   de créditos y de pagos.

## 🚧 Pendiente real (necesita credenciales o decisión)

1. **Config Railway** (arriba): `Anthropic__ApiKey` + credenciales MercadoPago. Sin esto, IA=503 y
   el pago queda en modo prueba.
2. **Probar el flujo real de MercadoPago** con una compra chica: checkout → pago →
   webhook (`/api/webhooks/mercadopago`) → créditos de descarga acreditados → saldo sube.
3. **Precios finales**: confirmar el mes ($15 ≈ S/56), cuántos créditos trae cada paquete
   (hoy 160/400/800) y el costo por diseño (`Design.CreditsCost`).
4. **Publicar el `.exe` V2**: `build-v2-seguro.ps1` → GitHub Releases → `Plugin__DownloadUrl`.

## 🐞 Infra de tests (menor, ajeno al producto)

`SimberDesigns.Tests` referencia un proyecto Web SDK (`SimberDesigns.Server`) y el runner no logra
CARGARLO en runtime (los 5 tests rojos son de HMAC/límites/embeddings, no de lógica nueva). Ya se
fuerza la copia del `.dll` + `FrameworkReference Microsoft.AspNetCore.App`, pero falta el cierre de
dependencias del web app en el runner. Fix limpio: usar `Microsoft.AspNetCore.Mvc.Testing`, o mover
los helpers probados (`HmacSignature`, `MembershipLimits`, `OnnxEmbeddingService`) a una **librería
de clases** aparte que las pruebas referencien sin cargar todo el web app. No bloquea el producto.

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

## 🔒 Seguridad (análisis anti-manipulación / Burp Suite) — 2026-09-14

**Modelo:** el `.exe` y el navegador son SIEMPRE inspeccionables por su dueño (con Burp + su propia CA
puede ver/editar SU tráfico). Por eso la seguridad real vive en el **servidor** (fuente de verdad) y en
la **firma de licencias** (ECDSA): un atacante NO puede forjar una licencia/suscripción sin la llave
privada (solo en el servidor). Interceptar y devolver un token falso no sirve: el cliente verifica la
firma con la pública embebida y lo rechaza.

**Arreglado (commit de esta fecha):** `POST /api/payments/confirm` ahora exige que el pago de MercadoPago
(a) tenga `external_reference == tx.Id` y (b) `transaction_amount >= monto de la orden`. Antes solo miraba
`status=approved`, así que con Burp se podía confirmar una orden cara reusando un `payment_id` ajeno o de
menor monto. Los montos/creditos NUNCA se toman del cliente: checkout, unlock, download y recarga usan el
precio/costo de la BD o del config del servidor.

**Config CRÍTICA (si no, hay hueco):** en producción `MercadoPago__AccessToken` DEBE ser real (no vacío,
no "dev-"); si queda vacío, `UseFakeCheckout=true` y `confirm` acredita SIN cobrar. Igual, `MercadoPago__
WebhookSecret` real para validar el webhook (hoy sin secret acepta todo).

**Refuerzos futuros (no hechos; requieren cuidado para no romper prod):**
- Rate limiting en login/activate/revalidate/ia — necesita `UseForwardedHeaders` primero, porque tras el
  proxy de Railway todos los clientes comparten IP y un límite por IP mal hecho bloquearía a todos.
- (Opcional) certificate pinning en el `.exe` — sube la barrera para inspeccionar la API, pero no cambia
  la seguridad de fondo (la firma ya protege). El HWID fuerte y el anti-rollback de reloj ya están.

## ⛔ No tocar
Firma/verificación de licencias (`Licensing/`), HWID, `AdminKeys`/`SimberKeys`, el `.exe` de V1,
los motores de armado (`mSimberArmado.bas` / `simberArmado.jsx`). La clave de Anthropic **jamás**
en el `.exe`: siempre server-side.
