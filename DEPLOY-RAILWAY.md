# SIMBER DESIGNS — Deploy en Railway (checklist completo)

La web (frontend Blazor + backend API + licencias + pagos) corre **junta** en un solo servicio Docker.
El `Dockerfile` y `railway.json` ya están listos. Esta guía deja TODO preparado para conectar tu cuenta.

---

## 🔐 Seguridad (ya resuelto en el código)
- La **llave privada** de licencias vive SOLO en el servidor (variable `Licensing__PrivateKey`). Nunca en el `.exe` ni en el repo.
- El `.exe` del cliente solo lleva la llave **pública** (no sirve para fabricar licencias).
- El **frontend Blazor** NO tiene ningún secreto → en F12 no se ve ninguna clave (verificado).
- Tu **API key de Anthropic** y los **tokens de Mercado Pago** viven solo en el servidor.
- **Cloudflare R2:** bucket **privado**. Los ZIP solo se bajan con URL firmada (vence ~10 min) tras validar login/créditos. Las keys R2 viven solo en Railway.

---

## 1) Base de datos (Postgres con pgvector)
El catálogo usa `vector` (pgvector). Railway's Postgres normal NO trae pgvector, así que:
- **Opción recomendada:** en Railway → New → Database → si ofrece "Postgres + pgvector" úsalo; si no, despliega el servicio Docker `pgvector/pgvector:pg16` como base de datos.
- Toma la **cadena de conexión** que te da Railway.
- Aplica el esquema una vez: abre la consola SQL de la BD y ejecuta el contenido de **`simber-designs-db-schema.sql`** (ya incluye la tabla `plugin_licenses` de las licencias del plugin).
- Si la BD ya existía, el arranque también aplica `ALTER … preview_r2_key` / `site_assets.r2_key`.

## 2) Crear el servicio web
- Railway → New Project → **Deploy from GitHub repo** → `ANTHONY109823/SIMBER_DESIGNS`.
- Railway detecta el `Dockerfile` (por `railway.json`). El puerto es **8080** (ya configurado).

## 3) Cloudflare R2 (obligatorio para vender ZIP sin gastar egress en Railway)

### Crear bucket
1. [Cloudflare Dashboard](https://dash.cloudflare.com) → **R2** → Create bucket (ej. `simber-designs`).
2. Déjalo **privado** (sin acceso público al bucket entero).
3. **Manage R2 API Tokens** → Create API token con permiso de lectura/escritura al bucket.
4. Anota: `Account ID`, `Access Key ID`, `Secret Access Key`.
5. `ServiceUrl` = `https://<ACCOUNT_ID>.r2.cloudflarestorage.com`

### Qué guarda cada sitio
| En R2 | En Postgres Railway |
|---|---|
| ZIP/RAR/CDR (`designs/…`) | Ficha del diseño + `r2_key` / `preview_r2_key` |
| Previews (`previews/…`) | Usuarios, créditos, compras, descargas |
| CMS fotos (`cms/…`) | Textos CMS (`site_content`) + fila `site_assets` (sin BYTEA si R2) |
| Instaladores (`installers/…`) | Licencias plugin |

### Variables Railway (R2)
| Variable | Valor | ¿Secreto? |
|---|---|---|
| `CloudflareR2__UseFakeClient` | **`false`** en producción | no |
| `CloudflareR2__AccountId` | Account ID de Cloudflare | no |
| `CloudflareR2__AccessKeyId` | Access Key del token R2 | 🔴 sí |
| `CloudflareR2__SecretAccessKey` | Secret del token R2 | 🔴 sí |
| `CloudflareR2__BucketName` | `simber-designs` | no |
| `CloudflareR2__ServiceUrl` | `https://<ACCOUNT_ID>.r2.cloudflarestorage.com` | no |
| `CloudflareR2__PublicBaseUrl` | (opcional) custom domain solo si expones previews/CMS; **no** uses esto para packs | no |
| `CloudflareR2__PresignedUrlExpiryMinutes` | `10` (packs) | no |
| `CloudflareR2__PreviewUrlExpiryMinutes` | `60` | no |

Con `UseFakeClient=false` y keys incompletas el servicio **no arranca** (a propósito).

### Prueba R2
1. Redeploy con las vars.
2. Admin → subir un diseño (~12 MB) + preview.
3. En el dashboard R2 deben aparecer `designs/…` y `previews/…`.
4. Descargar como cliente: la URL debe ser de `r2.cloudflarestorage.com` (o tu dominio), **no** un stream largo por Railway.
5. En Railway, la métrica **Salida** no debe crecer con cada ZIP.

> Dev local: deja `UseFakeClient=true` (archivos en `Uploads/` y `wwwroot/catalog-previews/`).

## 4) Variables de entorno (resto)
Usa doble guion bajo `__` para las secciones anidadas:

| Variable | Valor | ¿Secreto? |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | la cadena de tu Postgres de Railway | 🔴 sí |
| `Licensing__PrivateKey` | tu llave privada ECDSA (la de `admin-private-key.txt`) | 🔴 sí (crítico) |
| `Jwt__Key` | un texto aleatorio de 32+ caracteres | 🔴 sí |
| `Jwt__Issuer` | `SimberDesigns` | no |
| `Jwt__Audience` | `SimberDesigns.Client` | no |
| `Plugin__DownloadUrlCorel` / `Plugin__DownloadUrlIllustrator` | fallback si aún no subiste .exe por el panel admin | no |
| `MercadoPago__AccessToken` | Access Token real (`APP_USR-…`). Si ves `PA_UNAUTHORIZED_RESULT_FROM_POLICIES`, la cuenta/claves están bloqueadas: verifica identidad en MP, regenera token y cámbialo aquí. | 🔴 sí |
| `MercadoPago__PublicKey` | tu Public Key de Mercado Pago | no (es publicable) |
| `MercadoPago__WebhookSecret` | el secreto del webhook | 🔴 sí |
| `MercadoPago__PublicBaseUrl` | la URL pública **https://…** de Railway (obligatoria; sin esto el checkout falla y el pago queda “en proceso”) | no |
| `MercadoPago__UseFakeCheckout` | `false` (para cobrar de verdad) | no |
| `MercadoPago__UseSandbox` | `true` para pruebas / `false` producción | no |
| `ANTHROPIC_API_KEY` | tu API key de Anthropic (para la IA, Fase 2) | 🔴 sí |

> `ASPNETCORE_ENVIRONMENT=Production` y `ASPNETCORE_URLS=http://+:8080` ya los pone el Dockerfile.

## 5) Primer deploy y URL pública
- Deploy. Cuando termine, Railway te da una URL tipo `https://simber-designs-api-production.up.railway.app`.
- Pon esa URL en `MercadoPago__PublicBaseUrl` (redeploy).
- En el panel de Mercado Pago, configura el **webhook** apuntando a `https://<tu-url>/api/mercadopago/webhook` (o la ruta del `MercadoPagoWebhookController`).

## 6) El botón "Descargar" (V2)
1. Preferido: Admin → instaladores → sube Corel / Illustrator (van a R2 si está activo).
2. Alternativa: GitHub Releases + `Plugin__DownloadUrlCorel` / `…Illustrator`.
3. La descarga del `.exe` es **gratis**; el cobro es al **activar**.

## 7) Apuntar el `.exe` a la web (paso final)
El `.exe` V2 debe llamar a tu URL de Railway. En el repo del plugin, edita **una línea** en
`SimberDesigns/src/SimberDesigns.App/Licensing/WebLicenseClient.cs` (el valor de `DefaultBaseUrl` para Release)
poniendo tu URL real de Railway, y reconstruye V2 con el script endurecido (ver `build-v2-seguro` en el repo del plugin).
Vuelve a subir ese `.exe` (panel admin o Releases).

---

## ✅ Prueba de humo (cuando esté arriba)
1. Entra a la web pública → regístrate.
2. Paga el plan del plugin (Mercado Pago real o sandbox) → se crea la licencia.
3. Descarga el `.exe` con el botón.
4. Ábrelo → email + contraseña → **ENTRAR Y ACTIVAR** → debe activarse solo para tu PC.
5. Al mes, si pagas, renueva solo; si no, se bloquea al vencer.
6. Sube un diseño de prueba → aparece en R2 → descarga con URL firmada.
