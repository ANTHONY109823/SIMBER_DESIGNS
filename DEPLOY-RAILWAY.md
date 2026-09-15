# SIMBER DESIGNS — Deploy en Railway (checklist completo)

La web (frontend Blazor + backend API + licencias + pagos) corre **junta** en un solo servicio Docker.
El `Dockerfile` y `railway.json` ya están listos. Esta guía deja TODO preparado para conectar tu cuenta.

---

## 🔐 Seguridad (ya resuelto en el código)
- La **llave privada** de licencias vive SOLO en el servidor (variable `Licensing__PrivateKey`). Nunca en el `.exe` ni en el repo.
- El `.exe` del cliente solo lleva la llave **pública** (no sirve para fabricar licencias).
- El **frontend Blazor** NO tiene ningún secreto → en F12 no se ve ninguna clave (verificado).
- Tu **API key de Anthropic** y los **tokens de Mercado Pago** viven solo en el servidor.

---

## 1) Base de datos (Postgres con pgvector)
El catálogo usa `vector` (pgvector). Railway's Postgres normal NO trae pgvector, así que:
- **Opción recomendada:** en Railway → New → Database → si ofrece "Postgres + pgvector" úsalo; si no, despliega el servicio Docker `pgvector/pgvector:pg16` como base de datos.
- Toma la **cadena de conexión** que te da Railway.
- Aplica el esquema una vez: abre la consola SQL de la BD y ejecuta el contenido de **`simber-designs-db-schema.sql`** (ya incluye la tabla `plugin_licenses` de las licencias del plugin).

## 2) Crear el servicio web
- Railway → New Project → **Deploy from GitHub repo** → `ANTHONY109823/SIMBER_DESIGNS`.
- Railway detecta el `Dockerfile` (por `railway.json`). El puerto es **8080** (ya configurado).

## 3) Variables de entorno (⚠️ los secretos van aquí, NO en el repo)
Usa doble guion bajo `__` para las secciones anidadas:

| Variable | Valor | ¿Secreto? |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | la cadena de tu Postgres de Railway | 🔴 sí |
| `Licensing__PrivateKey` | tu llave privada ECDSA (la de `admin-private-key.txt`) | 🔴 sí (crítico) |
| `Jwt__Key` | un texto aleatorio de 32+ caracteres | 🔴 sí |
| `Jwt__Issuer` | `SimberDesigns` | no |
| `Jwt__Audience` | `SimberDesigns.Client` | no |
| `Plugin__DownloadUrl` | URL del `.exe` V2 (ver paso 5) | no |
| `MercadoPago__AccessToken` | tu Access Token real de Mercado Pago | 🔴 sí |
| `MercadoPago__PublicKey` | tu Public Key de Mercado Pago | no (es publicable) |
| `MercadoPago__WebhookSecret` | el secreto del webhook | 🔴 sí |
| `MercadoPago__PublicBaseUrl` | la URL pública **https://…** de Railway (obligatoria; sin esto el checkout falla y el pago queda “en proceso”) | no |
| `MercadoPago__UseFakeCheckout` | `false` (para cobrar de verdad) | no |
| `MercadoPago__UseSandbox` | `true` para pruebas / `false` producción | no |
| `CloudflareR2__UseFakeClient` | `true` (o configura R2 real si usas catálogo con archivos) | — |
| `ANTHROPIC_API_KEY` | tu API key de Anthropic (para la IA, Fase 2) | 🔴 sí |

> `ASPNETCORE_ENVIRONMENT=Production` y `ASPNETCORE_URLS=http://+:8080` ya los pone el Dockerfile.

## 4) Primer deploy y URL pública
- Deploy. Cuando termine, Railway te da una URL tipo `https://simber-designs-api-production.up.railway.app`.
- Pon esa URL en `MercadoPago__PublicBaseUrl` (redeploy).
- En el panel de Mercado Pago, configura el **webhook** apuntando a `https://<tu-url>/api/mercadopago/webhook` (o la ruta del `MercadoPagoWebhookController`).

## 5) El botón "Descargar" (V2)
1. Sube el **`.exe` de V2** (carpeta `ENTREGABLES.../V2 - WEB (con IA)/SIMBER DESIGNS.exe`) a **GitHub Releases** (Releases → Draft new release → adjunta el .exe) o a Cloudflare R2.
2. Copia el enlace de descarga directo y ponlo en la variable `Plugin__DownloadUrl`.
3. Listo: el botón **⬇ Descargar SIMBER DESIGNS** de `/programas` (que llama a `/api/plugin/download`) redirige a ese archivo. La descarga es **gratis**; el cobro es al **activar**.

## 6) Apuntar el `.exe` a la web (paso final)
El `.exe` V2 debe llamar a tu URL de Railway. En el repo del plugin, edita **una línea** en
`SimberDesigns/src/SimberDesigns.App/Licensing/WebLicenseClient.cs` (el valor de `DefaultBaseUrl` para Release)
poniendo tu URL real de Railway, y reconstruye V2 con el script endurecido (ver `build-v2-seguro` en el repo del plugin).
Vuelve a subir ese `.exe` a Releases y actualiza `Plugin__DownloadUrl`.

---

## ✅ Prueba de humo (cuando esté arriba)
1. Entra a la web pública → regístrate.
2. Paga el plan del plugin (Mercado Pago real o sandbox) → se crea la licencia.
3. Descarga el `.exe` con el botón.
4. Ábrelo → email + contraseña → **ENTRAR Y ACTIVAR** → debe activarse solo para tu PC.
5. Al mes, si pagas, renueva solo; si no, se bloquea al vencer.
