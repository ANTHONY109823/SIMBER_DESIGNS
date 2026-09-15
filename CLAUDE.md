# SIMBER web — enlace con el escritorio

Este repo es la **web/API** (Railway). El panel y los `.exe` viven en:

`C:\Users\ANTHONY\Desktop\PLUGIN ARMADO EN COREL\SimberDesigns`

**Lee primero** `PLUGIN ARMADO EN COREL\SimberDesigns\CLAUDE.md` si vas a
cambiar licencias, el token o cómo se activa el plugin.

- V1 (WhatsApp) ya está: dos `.exe` + KeyGen. No la deshagas desde aquí.
- V2: este servidor firma el token (`PluginController`) con la **misma**
  llave privada que el KeyGen (`Licensing__PrivateKey`).
- El firmador de aquí (`SimberDesigns.Server/Licensing/`) debe coincidir
  con el del escritorio. Si cambias el formato del token, cambia los dos.
- Deploy: `DEPLOY-RAILWAY.md`. Plan largo: `PLUGIN ARMADO EN COREL\INTEGRACION-WEB-PLAN.md`.
