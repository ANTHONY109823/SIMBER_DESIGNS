# Plan · Marketplace de diseñadores (Simber Designs)

**Estado:** especificación lista · **no implementado aún**  
**Repo web:** `SIMBER_DESIGNS` (misma app Blazor + API)  
**Fecha base:** 2026-09-15  
**Uso:** retomar mañana; ir agregando ideas al final mientras avanza el frontend.

Relacionado: `CLAUDE.md` (web) y `PLUGIN ARMADO EN COREL\SimberDesigns\CLAUDE.md` (pendiente del `.exe` — **no mezclar** con este plan).

---

## 1. Objetivo

Convertir Simber en un **ecosistema** (no solo tienda):

1. Catálogo (propios + diseñadores invitados)  
2. Plugin Corel / Illustrator  
3. Encárganos  
4. Marketplace con wallet, retiro rápido y reputación  

Ventaja vs competencia tipo VectorSport: diseño + programa + servicio + reputación en un solo lugar.

---

## 2. Decisiones de negocio (cerradas)

| Tema | Decisión |
|------|----------|
| Acceso diseñador (fase 1) | Solo **invitación** (2–3 personas). Misma app; registro diseñador **implementado pero cerrado**. |
| Acceso diseñador (fase 2) | **Solicitud manual** (“quiero ser diseñador” → admin aprueba). Mismo motor. |
| Página aparte de invite | **No.** Todo dentro de la web actual. |
| Comisión por venta | **15 % Simber / 85 % diseñador** |
| Base del split | Sobre **neto post Mercado Pago** si el pago es externo. Si es compra interna (wallet), 15/85 sobre el precio interno (sin MP). |
| Fee por retiro | **0 %** |
| Precio típico diseño marketplace | **S/ 10 – 20** |
| Quién compra | **Cualquier cliente** registrado (no solo diseñadores) |
| Moneda del comprador | Sigue el sistema de **créditos** actual |
| Moneda del diseñador (ganancias) | **Soles** en wallet |
| Compra con saldo | Diseñador puede **convertir soles → créditos** y comprar en la plataforma (diseños propios de Simber u otros diseñadores) |
| Retiro mínimo | **S/ 20** |
| Retiro máximo | **S/ 100 por día** (calendario Perú) |
| Velocidad de retiro | **Automático en minutos** (la página/sistema lo hace todo) |
| Facturación | **Aún no**; el ledger sí debe existir |
| Excel / procesos manuales de plata | **No** |
| Moderación de diseños | **Obligatoria** antes de publicar |
| Estrellas | Post-compra; solo quien compró (fase posterior al núcleo wallet) |

### Diseños de Simber (tuyos)

- No hay split a un “diseñador fantasma”.  
- Venta de diseño propio → **100 %** a Simber (tras fee MP si aplica).

---

## 3. Créditos ↔ soles (fórmula)

Paquete básico actual de referencia: **S/ 20 = 160 créditos** → **1 sol = 8 créditos**.

### Fórmula

**Créditos del diseño = precio en soles × 8**

| Precio (diseñador fija en S/) | Créditos (comprador ve / paga) |
|-------------------------------|--------------------------------|
| S/ 10 | **80** |
| S/ 15 | **120** |
| S/ 20 | **160** |

- El diseñador fija **soles**; el sistema calcula créditos (no edita créditos a mano).  
- Conversión wallet → créditos para re-gastar: misma tasa **1 sol = 8 créditos** (piloto).  
- “Gratis del día” / promos propias de Simber pueden seguir aparte.  
- Diseños viejos a 2 créditos: no usar esa lógica en marketplace; marketplace siempre ×8.

### Ejemplo venta S/ 20 (pago Mercado Pago)

| Concepto | Monto aprox. |
|----------|----------------|
| Bruto | 20,00 |
| Fee MP (~4,5 %) | −0,90 |
| Neto | 19,10 |
| Simber 15 % | ~2,87 |
| Diseñador 85 % | ~16,24 → wallet |
| Comprador | gasta **160 créditos** |

*(Ajustar % real de MP según el panel de la cuenta.)*

---

## 4. Flujos

### 4.1 Invitación (fase 1)

1. Admin genera código / marca usuario como diseñador.  
2. Persona se registra o enlaza cuenta en la **misma** app.  
3. Completa perfil + datos de cobro (Yape/CCI a su nombre).  
4. Estado: **verificado** → puede subir diseños.

### 4.2 Publicar diseño

1. Preview + archivo CDR + precio S/ (10–20).  
2. Estado: `pending`.  
3. Admin aprueba / rechaza.  
4. Si aprueba: `live` en catálogo; créditos = precio × 8.

### 4.3 Venta externa (cliente paga MP / recarga créditos y descarga)

1. Flujo actual de créditos + unlock, o cobro en soles si más adelante se unifica.  
2. Al confirmar la venta marketplace: asiento ledger 15/85.  
3. Diseñador recibe soles en wallet (disponible para retiro según reglas).

### 4.4 Venta / uso interno (saldo)

1. Diseñador convierte soles → créditos **o** compra con wallet según regla implementada.  
2. Sin fee MP.  
3. Split 15/85 si el vendedor es otro diseñador; 100 % Simber si el diseño es propio.

### 4.5 Retiro

1. Diseñador pide retiro (monto entre 20 y lo disponible, respetando tope diario 100).  
2. Sistema verifica anti-fraude.  
3. Si OK → payout en minutos, fee 0 %.  
4. Si falla → estado `needs_review` con motivo (sin Excel).

---

## 5. Seguridad y anti-fraude (obligatorio)

Principio: **el front no manda el saldo ni el monto “de verdad”**. Solo solicita; el servidor calcula.

1. Saldo = suma del **ledger** en servidor.  
2. Ledger **inmutable** (no editar filas de venta a mano).  
3. **Idempotencia** de payout (un ID = un solo pago).  
4. Tope **S/ 100/día** contado en servidor.  
5. Retiro ≤ disponible y ≥ 20, en transacción con bloqueo de fila.  
6. JWT + rol Designer **verificado**.  
7. Rate limit en endpoint de retiro.  
8. Cambiar CCI/Yape → enfriamiento (ej. 24 h) antes de retirar.  
9. Auditoría: IP, user-agent, montos, saldo antes/después.  
10. Chargeback MP → debitar wallet; si no alcanza → bloquear cuenta.  
11. Bloquear auto-compra fraudulenta (misma persona infla ventas para retirar).  
12. Solo diseños `live` aprobados generan saldo.

---

## 6. Qué construir (checklist técnico)

### Núcleo (prioridad 1 — mañana / primeros sprints)

- [ ] Invite / flag `CanSell` + rol Designer (misma app, cerrado)  
- [ ] Perfil diseñador + datos de cobro  
- [ ] Subida diseño → cola moderación → live  
- [ ] Precio S/ → créditos automáticos (×8)  
- [ ] `LedgerEntry` + wallet (disponible)  
- [ ] Split 15/85 al confirmar venta marketplace  
- [ ] Pedir retiro (min 20, max 100/día, fee 0, auto)  
- [ ] Admin: invitaciones, moderar, ver ledger, retiros fallidos  

### Siguiente oleada

- [ ] Convertir soles → créditos (re-gastar en plataforma)  
- [ ] Estrellas 1–5 post-compra + perfil diseñador  
- [ ] Solicitud manual “quiero ser diseñador” (fase 2)  
- [ ] Notificaciones email/WhatsApp (venta, retiro, rechazo)  
- [ ] Topes extra anti-fraude primeros 30 días  

### Fuera de alcance por ahora

- [ ] Facturación electrónica  
- [ ] Signup abierto masivo de diseñadores  
- [ ] Fee por retiro  
- [ ] Excel / liquidación manual  
- [ ] Cambios del `.exe` / activación Adobe (otro doc en CLAUDE escritorio)  

---

## 7. Orden de implementación sugerido

1. Invite + perfil + moderación de diseños  
2. Ledger + wallet + split 15/85 (ventas)  
3. Retiro automático (20 / 100 día)  
4. Conversión soles → créditos  
5. Estrellas  
6. Solicitud manual de diseñadores  

Mientras tanto se puede avanzar **frontend** de catálogo/UI; este doc se va ampliando abajo.

---

## 8. Texto corto para acuerdo con diseñadores invitados

> Ganas en soles. Simber se queda el **15 %** de cada venta (después de la comisión de Mercado Pago si el comprador paga afuera). El **retiro es gratis**, mínimo **S/ 20**, máximo **S/ 100 al día**, y se acredita en minutos a tu Yape/CCI verificado. También puedes usar tu saldo para comprar créditos y bajar diseños de la plataforma. Todo diseño pasa por aprobación antes de publicarse. Declaras que tienes derechos sobre lo que subes.

---

## 9. Dependencias / contexto actual de la web

Ya existe (reutilizar, no romper):

- Usuarios, JWT, roles Admin/Customer  
- Catálogo, créditos, paquetes (160/S20, 400/S30, 800/S50)  
- Mercado Pago checkout + fulfillment  
- Admin catálogo / contenido / installers  
- Login cliente ≠ admin  

Pendiente externo (no este plan): activación V2 del `.exe` tipo Adobe — ver `SimberDesigns/CLAUDE.md`.

---

## 10. Ideas para agregar después (bitácora)

_Ir completando mientras avanzamos el frontend:_

- [ ]  
- [ ]  
- [ ]  

---

## 11. Resumen en una frase

**Misma app, diseñadores por invitación, 15 % por venta, retiro gratis en minutos (mín. 20 / máx. 100 día), compradores en créditos (×8), diseñadores en soles con opción de re-gastar; todo automatizado con ledger y anti-fraude; sin Excel ni factura todavía.**
