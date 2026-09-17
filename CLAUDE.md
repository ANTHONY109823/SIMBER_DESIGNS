# SIMBER web — enlace con el escritorio

Este repo es la **web/API** (Railway). El panel y los `.exe` viven en:

`C:\Users\ANTHONY\Desktop\PLUGIN ARMADO EN COREL\SimberDesigns`

**Lee primero** `PLUGIN ARMADO EN COREL\SimberDesigns\CLAUDE.md` (sección
**PENDIENTE · Activación V2 tipo Adobe / Cursor**). Ahí está el checklist del
`.exe` y el resumen de lo ya hecho en la web.

- V1 (WhatsApp) ya está: dos `.exe` + KeyGen. No la deshagas desde aquí.
- V2: este servidor firma el token (`PluginController`) con la **misma**
  llave privada que el KeyGen (`Licensing__PrivateKey`).
- El firmador de aquí (`SimberDesigns.Server/Licensing/`) debe coincidir
  con el del escritorio. Si cambias el formato del token, cambia los dos.
- Deploy: `DEPLOY-RAILWAY.md`. Plan largo: `PLUGIN ARMADO EN COREL\INTEGRACION-WEB-PLAN.md`.

### Web ya hecha (no rehacer)

- `/programas`: solo Descargar Corel (verde) / Illustrator (naranja); sin precios.
- Mes plugin: US$15 → PEN con `UsdToPenRate` (Railway).
- Nav: Encárganos (público) / Recarga créditos (cliente logueado).
- Catálogo: todos los diseños; registro obligatorio para descargar; Gratis del
  día debajo de Buscar por foto (sin flash de filtros antes).
- Login cliente ≠ admin. Salir sin “Acceso restringido”.
- Home: hero fullscreen + carrusel vertical desktop.
- **R2:** ZIP + previews + CMS + instaladores en Cloudflare R2 (prod).
  Postgres solo metadatos. Activar: `CloudflareR2__UseFakeClient=false` + keys
  (ver `DEPLOY-RAILWAY.md` §3). Dev local puede quedar en FakeClient.

### Pendiente solo en el .exe (cuando Anthony lo pida)

Login al abrir, bloqueo sin pago/internet, activate HWID+Edition, renew.
**No tocar** firma / HWID / FileClockGuard / KeyGen V1.

### Marketplace diseñadores (especificación)

Plan completo (reglas, créditos×8, retiros, seguridad, checklist):
**`MARKETPLACE-DISENADORES-PLAN.md`**. Aún no implementado; retomar mañana / al avanzar frontend.
