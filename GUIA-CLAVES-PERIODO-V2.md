# Guía: claves de periodo V2 (SMK-…) — qué se hizo

**Para Claude / agentes:** léeme antes de tocar licencias, pagos MP, admin de
licencias o la ventana de activación del `.exe`. Esto **ya está implementado**
(sept 2026). No rehacer el diseño ni volver al modelo “solo +30 días al pagar”.

Código web/API: `C:\Users\ANTHONY\Desktop\SIMBER_DESIGNS`  
Código `.exe`: `C:\Users\ANTHONY\Desktop\PLUGIN ARMADO EN COREL\SimberDesigns`

---

## Idea en una frase

El cliente **paga** (Mercado Pago o Yape/sorteo vía Anthony) → recibe un **serial
tipo antivirus** (`SMK-COR-…` / `SMK-ILU-…`) → **canjea** (web o `.exe`) → eso
suma N días a `plugin_licenses` → luego **activate** ata el HWID y firma el token.

Duración: **no solo 1 mes**. MP ofrece 1/3/6/12 meses. Admin puede emitir
**cualquier cantidad de días** (1–730).

---

## Flujo (3 caminos)

### A) Mercado Pago (cliente elige duración)

1. Cliente logueado en `/programas#planes` elige Corel o Illustrator × 1/3/6/12 meses.
2. `POST /api/payments/checkout` con `kind` + `months` → nota `plugin:{días}:{Edition}`.
3. Al acreditar (`PaymentFulfillmentService`): **no** extiende la licencia directo;
   llama `IPluginPeriodKeyService.IssueAsync` → fila `plugin_period_keys` Status=`Pending`.
4. Cliente ve el serial en **Mi cuenta** → Canjear, o lo pega en el `.exe` al entrar.
5. Canje → suma días a `plugin_licenses` → Entrar y activar → HWID + token firmado.

Precios: US$15 × meses (cobro en PEN vía MP). Días: 30 / 90 / 180 / 365.

### B) Admin — pago directo / Yape / transferencia

1. Anthony cobra fuera de MP.
2. `/admin/licenses`:
   - **Cliente existente:** “Emitir clave” (email + Corel/Illustrator + días + nota).
   - **Nuevo (sorteo):** “Crear cliente + clave” (email, temporal, forzar cambio de
     contraseña, días, nota).
3. WhatsApp: link descarga `.exe` + email + temporal + serial `SMK-…`.
4. Cliente canjea / entra en el `.exe` igual que en A.

### C) Sorteo / cuenta regalada

Igual que B con `MustChangePassword=true`. Al login web → `/cuenta?cambiar=1`.
Al cambiar contraseña se limpia el flag (`AccountController`).

---

## Tablas / modelo

| Tabla / campo | Rol |
|---|---|
| `plugin_period_keys` | Seriales Pending / Redeemed / Revoked. `days`, `edition`, `source` (`manual` \| `mercadopago`), `note`, `transaction_id` |
| `plugin_licenses` | Periodo vigente por usuario+edition; `expires_at`, `hardware_id` (null hasta activate) |
| `users.must_change_password` | Cuentas creadas por admin (sorteo) |

Serial: `SMK-COR-XXXXXXXX` o `SMK-ILU-XXXXXXXX` (alfanumérico sin confusos).

Migración: `DbInitializer.EnsureCommerceSchemaAsync` crea la tabla y la columna
(no hace falta migración EF aparte en este proyecto).

---

## Archivos clave (web)

| Archivo | Qué hace |
|---|---|
| `SimberDesigns.Server/Models/Entities.cs` | `PluginPeriodKey`, `MustChangePassword` |
| `SimberDesigns.Server/Services/PluginPeriodKeyService.cs` | Emitir + canjear (suma días) |
| `SimberDesigns.Server/Services/PluginCheckoutPlans.cs` | Planes 1/3/6/12 y parseo de `kind` |
| `SimberDesigns.Server/Services/PaymentFulfillmentService.cs` | MP → `IssueAsync` (ya no +30 fijo) |
| `SimberDesigns.Server/Controllers/PaymentsController.cs` | Storefront con planes; checkout con `months` |
| `SimberDesigns.Server/Controllers/PluginController.cs` | `GET keys`, `POST redeem`, `activate` |
| `SimberDesigns.Server/Controllers/AdminController.cs` | `POST customers`, `POST licenses/issue`, `GET period-keys` |
| `SimberDesigns.Client/Pages/AdminLicenses.razor` | UI crear/emitir + tablas |
| `SimberDesigns.Client/Pages/Programs.razor` | `#planes` botones MP |
| `SimberDesigns.Client/Pages/Account.razor` | Lista/canje de seriales |

## Archivos clave (`.exe`)

| Archivo | Qué hace |
|---|---|
| `Licensing/WebLicenseClient.cs` | `RedeemAsync(code, hwid, edition)` |
| `ActivationWindow.xaml(.cs)` | Si pega `SMK-…` + login → redeem+token; si no, `ActivateAsync` |

V1 (`SIMBER_V1`) sigue con token KeyGen; no usa SMK-.

---

## API rápida

```
POST /api/payments/checkout   { kind: "plugin-corel", months: 3 }
GET  /api/payments/storefront → PluginPlans[]

POST /api/admin/customers     crear usuario (+ opcional Issue)
POST /api/admin/licenses/issue { email, edition, days, note }
GET  /api/admin/period-keys

GET  /api/plugin/keys
POST /api/plugin/redeem       { code, hardwareId?, edition? }
POST /api/plugin/activate     { hardwareId, edition }   // requiere periodo ya canjeado
```

Notas de transacción plugin: `plugin:{días}:{Corel|Illustrator}`  
(compat: `plugin:month-1pc:Corel` se lee como 30 días).

---

## Qué NO tocar / no confundir

- **No** reescribir firma ECDSA / HWID / `FileClockGuard` / formato `SIMBER.<payload>.<firma>`.
- **No** mezclar con V1 KeyGen (WhatsApp). V1 no usa `plugin_period_keys`.
- **No** volver a extender licencia solo en fulfill de MP sin emitir serial: el
  contrato actual es serial → canje → activate.
- Corel e Illustrator siguen siendo **cobros y filas separados**.
- Tras canje sin HWID, `activate` sigue siendo el que ata la PC.

---

## Pendiente ops (no es código de este diseño)

1. **Deploy** de la web a Railway (schema se aplica al arrancar).
2. **Rebuild** entregas V2 (`./build-entregas-v2.ps1`) y subir `.exe` al admin
   para que el canje SMK- en el cliente de producción esté activo.
3. Probar en prod: admin emite → canje cuenta → login `.exe` → revalidate.

---

## Checklist mental si algo “no activa”

1. ¿Existe fila Pending/Redeemed en `plugin_period_keys` para ese email?
2. ¿Se canjeó? (`Redeemed`) → ¿`plugin_licenses.expires_at` > now?
3. ¿Edition del serial coincide con el `.exe` (Corel vs Illustrator)?
4. ¿HWID ya atado a otra PC? → 409.
5. ¿Web vieja en Railway / `.exe` viejo sin `RedeemAsync`?
