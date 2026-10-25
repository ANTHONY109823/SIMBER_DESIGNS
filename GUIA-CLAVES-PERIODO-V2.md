# Guía: claves de periodo V2 (serial 16 chars) — qué se hizo

**Para Claude / agentes:** léeme antes de tocar licencias, pagos MP, admin de
licencias o la ventana de activación del `.exe`. Esto **ya está implementado**
(sept 2026). No rehacer el diseño ni volver al modelo “solo +30 días al pagar”.

Código web/API: `C:\Users\ANTHONY\Desktop\SIMBER_DESIGNS`  
Código `.exe`: `C:\Users\ANTHONY\Desktop\PLUGIN ARMADO EN COREL\SimberDesigns`

---

## Idea en una frase

El cliente **paga** (Mercado Pago o Yape/sorteo vía Anthony) → recibe un **serial
alfanumérico de 16 caracteres** → **canjea en el `.exe`** (tras login) → eso
suma N días a `plugin_licenses` → **activate** ata el HWID y firma el token.

Duración: **no solo 1 mes**. MP ofrece 1/3/6/12 meses. Admin puede emitir
**cualquier cantidad de días** (1–730).

---

## Flujo (cliente)

1. Se registra / inicia sesión en la **web** (solo email + contraseña).
2. Paga el plan → ve el serial de 16 chars (Mi cuenta / modal de pago).
3. Descarga el `.exe` → **login solo email + contraseña** (NO pide serial ahí).
4. Si no hay periodo activo, el `.exe` pide el serial → canje + activa esa PC.

Login web y login del `.exe` **nunca** piden SMK- ni el serial en la misma
pantalla que la contraseña.

---

## Flujo (3 caminos)

### A) Mercado Pago (cliente elige duración)

1. Cliente logueado en `/programas#planes` elige Corel o Illustrator × 1/3/6/12 meses.
2. `POST /api/payments/checkout` con `kind` + `months` → nota `plugin:{días}:{Edition}`.
3. Al acreditar (`PaymentFulfillmentService`): **no** extiende la licencia directo;
   llama `IPluginPeriodKeyService.IssueAsync` → fila `plugin_period_keys` Status=`Pending`.
4. Cliente ve el serial en **Mi cuenta** (y al pagar).
5. En el `.exe`: login → pega serial → suma días → HWID + token firmado.

### B) Admin — pago directo / Yape / transferencia

1. Anthony cobra fuera de MP.
2. `/admin/licenses` emite clave (cliente existente o nuevo + temporal).
3. WhatsApp: link `.exe` + email + temporal + serial (16 chars).
4. Cliente entra en el `.exe` igual que en A.

### C) Sorteo / cuenta regalada

Igual que B con `MustChangePassword=true`. Al login web → `/cuenta?cambiar=1`.

---

## Serial

- **Actual:** 16 caracteres alfanuméricos (sin prefijo; sin O/0/I/1 confusos).
  La edición (Corel/Illustrator) vive en la fila `plugin_period_keys`, no en el código.
- **Legado:** `SMK-COR-…` / `SMK-ILU-…` aún se reconocen al canjear.

---

## Archivos clave (`.exe`)

| Archivo | Qué hace |
|---|---|
| `Licensing/WebLicenseClient.cs` | `RedeemAsync(code, hwid, edition)` |
| `ActivationWindow.xaml(.cs)` | Login email+pass → si 402, pide serial 16 chars → redeem+token |

V1 (`SIMBER_V1`) sigue con token KeyGen; no usa seriales de periodo.

---

## Qué NO tocar

- Firma ECDSA / HWID / `FileClockGuard` / formato `SIMBER.<payload>.<firma>`.
- No mezclar con V1 KeyGen.
- No pedir serial en la misma pantalla de login.
- Corel e Illustrator = cobros y filas separados.
