# PRD — Evolutivo Kick: DustPlanner en el chat y en el directo

> Evolutivo de DustPlanner 1.0.0. Fecha: 17/10/2026. Alcance: especificación para implementar, por etapas.
> Acompaña a [`PROPUESTA.md`](PROPUESTA.md). Todo lo de su §10 sigue vigente (cero automatización, MIT, sin datos de
> Blizzard en el repo, conservador ante lo que no se pueda verificar). Si algo de acá contradice el §10, manda el §10.

---

## 0. Resumen

- **Qué es.** DustPlanner ya sabe en vivo el rango, el récord de la sesión y del día, la racha y la partida en curso
  (`/api/state`). Este evolutivo lleva esos datos al **chat de Kick** con comandos (`!deck`, `!rank`, `!wr`, `!last`),
  a una **página pública del mazo** y a **eventos del directo**: resumen al cortar, aviso para resolver la predicción y
  canjes de puntos en el overlay.
- **Por qué hay hueco.** HDT tiene una extensión de Twitch para que el viewer vea tu mazo. En Kick no hay equivalente
  ni sistema oficial de extensiones. Lo que sí hay es una API pública con OAuth 2.1, webhooks, envío de mensajes de
  chat y recompensas de canal.
- **Dos caminos, en este orden:**
  1. **Camino local (etapas K1–K2).** El servicio expone respuestas de texto listas para el chat en `127.0.0.1`.
     Streamer.bot u otro bot las pide y las publica. Tsukuru lo usa en sus directos desde el primer fin de semana.
     No hace falta infraestructura nueva.
  2. **Camino nativo (etapas K3–K8).** Un botón **"Conectar con Kick"** en el panel, sin Streamer.bot. Es el producto
     para que cualquier streamer lo instale sin tocar nada técnico, que es el objetivo del §9 de PROGRESO.
     Necesita un servicio en la nube chico: un Worker de Cloudflare. El motivo es que Kick exige un `client_secret` al
     canjear y renovar tokens, y manda los webhooks a **una única URL pública por app**.
- **Recomendación de arquitectura:**
  - Un Worker en `live.dustplanner.app` recibe y verifica los webhooks, guarda los tokens cifrados y reenvía al
    servicio local solo los comandos y eventos que importan, por un WebSocket saliente.
  - El servicio local (`dustplanner-host`, .NET 10) responde con los mismos textos del camino local.
  - El plugin de HDT solo suma una cosa: exportar el **mazo activo**.

---

## 1. Estado actual que este evolutivo reutiliza

Relevado en el repo el 17/10/2026 (commit `9a947ce`, release 1.0.0, 124 tests).

| Pieza | Qué hay hoy | Qué le falta para Kick |
|---|---|---|
| Plugin HDT (`DustPlanner.HdtExporter`, net472) | Exporta colección, mazos y rango al inbox. Lanza el servicio. `IHdtGateway.GetActiveDeck()` ya devuelve nombre + deckstring del mazo activo (lo usa el menú *What am I missing for the selected deck?*) | Exportarlo a disco: hoy solo se usa para abrir el panel |
| Contrato (`DustPlanner.Contracts`, netstandard2.0) | `CollectionExport`, `DecksExport`, `RankExport`, `DiagnosticsExport`, con `schema_version` | Un `ActiveDeckExport` |
| Servicio (`DustPlanner.Host`, net10, loopback) | Panel en `/`, API `/api/*`, overlay `/overlay`, SSE `/events`, `OverlayState` con `Rank`, `Dust`, `Session`, `Today` (con `WinRate` y `Streak`), `CurrentGame`, `LastGame` | El mazo activo en el estado. Textos para el chat. Conector con el Worker |
| Base SQLite (`DustPlanner.Store`) | `game` con `deck_id NULL`, `rank_sample`, migraciones embebidas | `stream_session`. Completar `game.deck_id` con el mazo activo |
| MCP (`DustPlanner.Mcp`) | 5 tools de solo lectura | Una tool de sesiones de directo |
| Sitio | GitHub Pages en `dustplanner.app` (apex, DNS en Cloudflare en modo *DNS only*) | El Worker va en un **subdominio** para no tocar Pages |

---

## 2. Kick: lo que la API permite hoy

Verificado contra la documentación oficial ([KickEngineering/KickDevDocs](https://github.com/KickEngineering/KickDevDocs),
fuente de docs.kick.com, último commit 05/10/2026) y contra KickLib, un cliente C# activo (commit del 08/10/2026).
La especificación OpenAPI (`https://api.kick.com/swagger/doc.yaml`) no se pudo leer desde el entorno de análisis.
Los nombres de campos que salen de ahí están marcados con **(OpenAPI)** y se confirman en K0.

### 2.1 OAuth 2.1

| Punto | Valor |
|---|---|
| Servidor | `https://id.kick.com` (distinto del de la API, `https://api.kick.com`) |
| Autorizar | `GET /oauth/authorize` con `client_id`, `response_type=code`, `redirect_uri`, `state` (obligatorio por ahora), `scope`, `code_challenge`, `code_challenge_method=S256` |
| Canjear código | `POST /oauth/token` (form-urlencoded): `code`, `client_id`, **`client_secret`**, `redirect_uri`, `grant_type=authorization_code`, `code_verifier` |
| Renovar | `POST /oauth/token`: `refresh_token`, `client_id`, **`client_secret`**, `grant_type=refresh_token`. Devuelve access **y** refresh nuevos |
| Token de app | `grant_type=client_credentials` (solo datos públicos) |
| Revocar | `POST /oauth/revoke?token=…&token_hint_type=access_token\|refresh_token` |
| Introspección | `POST /oauth/token/introspect` con `Authorization: Bearer` (la de `/public` está deprecada) |

**Consecuencia de diseño:** el secreto es obligatorio en el canje y en cada renovación. No puede viajar en el plugin
ni en el servicio local de terceros. Tiene que vivir en un servidor: el Worker.

### 2.2 Scopes que usa DustPlanner

| Scope | Para qué | Etapa |
|---|---|---|
| `user:read` | Saber qué usuario autorizó (`user_id`). El endpoint también devuelve `email`, que **nunca se guarda** | K3 |
| `channel:read` | Leer el slug del canal (`GET /public/v1/channels`; el endpoint de usuarios no lo trae) | K3 |
| `events:subscribe` | Webhooks de chat y de estado del directo | K3 |
| `chat:write` | Responder comandos y publicar el resumen | K5 |
| `channel:rewards:read` | Leer recompensas. Que también haga falta para el webhook de canjes se confirma en K0 | K7 |

No se piden `channel:write`, `streamkey:read`, `moderation:*`, `ads:*` ni `channel:rewards:write`. **Los cinco
scopes se piden todos juntos desde K3**, aunque algunos se usen recién en K5 o K7. Así nadie tiene que volver a
autorizar a mitad del evolutivo, y antes de K8 solo lo usan canales de la lista permitida. Además, el Durable Object
compara el `scope` que devuelve el token con el conjunto requerido. Si falta alguno, marca el canal con
`needs_reconnect` (pasa si en el futuro se suma un scope).

### 2.3 Webhooks

- **Configuración:** por app, en *kick.com/settings/developer → Edit → Enable Webhooks*, con **una URL pública**.
  `localhost` no sirve.
- **Suscripción:** `POST /public/v1/events/subscriptions` con `{"events":[{"name":"chat.message.sent","version":1}, …],
  "method":"webhook"}`. También existen `GET` y `DELETE` en la misma ruta. Con el token del usuario, la suscripción
  queda sobre su canal (**OpenAPI**: confirmar si hace falta `broadcaster_user_id`).
- **Eventos que usamos:**
  - `chat.message.sent` v1: `message_id`, `broadcaster`, `sender` (`user_id`, `username`, `identity.badges[]`),
    `content`, `emotes`, `replies_to`, `created_at`.
  - `livestream.status.updated` v1: `is_live`, `title`, `started_at`, `ended_at`.
  - `channel.reward.redemption.updated` v1: `id`, `user_input`, `status` (`pending`, `accepted` o `rejected`),
    `reward{id,title,cost}`, `redeemer`.
- **Cabeceras:** `Kick-Event-Message-Id` (ULID, clave de idempotencia), `Kick-Event-Subscription-Id`,
  `Kick-Event-Signature`, `Kick-Event-Message-Timestamp`, `Kick-Event-Type`, `Kick-Event-Version`.
- **Firma:** RSA PKCS#1 v1.5 con SHA-256 sobre `"{message_id}.{timestamp}.{raw_body}"`, en base64. La clave pública
  se pide a `https://api.kick.com/public/v1/public-key` y **no se hardcodea**, porque Kick puede rotarla.
- **Baja automática:** si la app falla al procesar un evento durante más de un día, Kick la desuscribe de ese evento
  y hay que volver a suscribirse. Al desactivar los webhooks de la app se cae todo.
- **Límite de suscripciones de chat:** 1.000 por app sin verificar y 10.000 verificada. La verificación se pide por
  mail a developers@kick.com.

### 2.4 Chat

`POST /public/v1/chat` con `content`, `type` (`bot` o `user`), `broadcaster_user_id` y `reply_to_message_id`.
Requiere `chat:write`. La documentación dice solo que se puede escribir "como cuenta Bot o como tu cuenta de
usuario". Hay tres cosas que se confirman en K0 **(OpenAPI)**:

- Qué cuenta firma los mensajes con `type: bot`. Lo esperable es que sea la cuenta dueña de la app, por eso la app se
  crea desde una cuenta dedicada.
- Cuándo `broadcaster_user_id` es obligatorio. KickLib no lo manda en los mensajes de bot.
- Los límites de frecuencia.

KickLib documenta un máximo de 500 caracteres por mensaje.

### 2.5 Lo que la API no tiene

- **Predicciones y encuestas:** no hay endpoint ni scope. Las predicciones se crean y resuelven a mano con
  `/prediction` y requieren los puntos de canal activados. DustPlanner solo puede **avisar** que terminó la partida.
- **Leer chat sin webhook:** existe un WebSocket de Pusher que usan varias librerías, pero no es oficial. **No se usa.**

---

## 3. Enfoques y comparación

Restricción común: **nada toca el cliente de Hearthstone.** Kick solo recibe mensajes de chat, y solo cuando un viewer
o el directo los dispara.

### A) Solo local con un bot de terceros (Streamer.bot)
El servicio expone `GET /api/chat/{comando}` en texto plano y el bot hace el resto.

### B) Integración nativa sin servidor, usando el WebSocket no oficial
Lee el chat por Pusher y escribe con el token del usuario. **Se descarta:** depende de algo no oficial y el token
igual necesita el secreto para renovarse.

### C) Cada streamer crea su propia app de Kick
Usa un redirect local (la doc de Kick recomienda `localhost` y documenta un rodeo para `127.0.0.1`). **Se descarta
como producto:** pedirle a un streamer no
técnico que active 2FA, cree una app, publique una URL de webhooks y pegue un secreto es justo lo que el §9 de PROGRESO
quiere evitar.

### D) Híbrido: endpoints locales + Worker propio (**recomendado**)
A queda como la base y como respaldo. El Worker resuelve OAuth, webhooks y envío. El servicio local sigue siendo el
dueño de los datos y de los textos.

| Criterio | A) Local + bot | B) Pusher | C) App propia | **D) Híbrido** |
|---|---|---|---|---|
| Esfuerzo | Bajo | Medio | Medio | Alto, por etapas |
| Usable por un streamer no técnico | No: configurar Streamer.bot | Sí | No | **Sí** |
| Depende de algo no oficial | No | **Sí** | No | No |
| Secreto expuesto | No aplica | Sí | En la PC del streamer | **Solo en el Worker** |
| Infraestructura | Ninguna | Ninguna | Ninguna | Worker + Durable Objects (plan gratis) |
| Valor para Tsukuru en días | **Sí** | — | — | Sí, vía A |

---

## 4. Arquitectura

```
 HDT ──► DustPlanner.HdtExporter ──► inbox/<cuenta>/active-deck.json   (nuevo; además de colección, mazos y rango)
                                            │
                                            ▼
 ┌──────────────────────────── dustplanner-host (127.0.0.1) ─────────────────────────────┐
 │ OverlayStateService ── estado + ActiveDeck ──► /api/state, /events, /overlay          │
 │ ChatResponder (DustPlanner.Kick) ── textos ──► GET /api/chat/{cmd}  ◄── Streamer.bot  │  camino local (K1–K2)
 │ KickConnector ── WebSocket saliente ──────────────────────────────┐                   │
 └───────────────────────────────────────────────────────────────────┼───────────────────┘
                                                                     │ wss://live.dustplanner.app/ws
 ┌──────────────────────── Cloudflare Worker: live.dustplanner.app ───┼───────────────────┐
 │ /connect, /oauth/callback  ── OAuth 2.1 + PKCE, client_secret ──► id.kick.com          │
 │ /webhooks/kick ── verifica firma, deduplica, filtra ──► Durable Object del canal ──────┘
 │ Durable Object por canal (SQLite): tokens cifrados, dispositivos, mazo publicado,      │
 │   WebSocket con hibernación; envía chat ──► api.kick.com/public/v1/chat               │
 │ /d/<slug> ── página pública del mazo (opt-in)                                          │
 └────────────────────────────────────────────────────────────────────────────────────────┘
```

**Decisiones:**

1. **Los textos se arman en un solo lugar:** `ChatResponder`, en la nueva librería `DustPlanner.Kick` (net10). Lo usan
   los endpoints locales (K2) y el conector (K5). El Worker nunca conoce rango, récord ni colección; solo transporta.
2. **El Worker guarda lo mínimo:** tokens de Kick cifrados (AES-GCM con una clave en `wrangler secret`), hash de los
   tokens de dispositivo, la configuración pública del canal y el último mazo publicado si la página está activa.
   **No guarda mensajes de chat.** Reenvía solo los que empiezan con el prefijo y un comando que el cliente declaró;
   el resto se descarta en memoria. Para filtrar **antes** de tocar el Durable Object, la configuración de cada canal
   (prefijo, comandos y qué eventos reenviar) se replica en Workers KV con clave `broadcaster_user_id`. Si un canal no
   tiene configuración, porque nunca se conectó un dispositivo, se descarta todo salvo `livestream.status.updated`.
3. **Un Durable Object por canal** (`idFromName(broadcaster_user_id)`). Es el plan gratuito con backend SQLite y usa
   WebSocket con hibernación para no facturar duración cuando no hay tráfico.
4. **El servicio local abre la conexión hacia afuera.** No se abre ningún puerto, el host sigue escuchando solo en
   loopback y sin internet todo lo local sigue funcionando.
5. **Credencial local:** un token de dispositivo opaco, emitido por el Worker al emparejar. Se guarda cifrado con
   DPAPI (usuario actual) en `%LocalAppData%\DustPlanner\kick.json`. Nunca se guardan tokens de Kick en la PC.
6. **El plugin sigue siendo mínimo:** suma la exportación del mazo activo y nada más. Nada de red en el plugin.
7. **Lenguajes:** el Worker en TypeScript, en `cloud/kick-worker/`, con wrangler y vitest con el pool de Workers. Todo
   lo demás en C#. La convención del §10.4 se mantiene: código, identificadores y documentación nueva en inglés. Este
   PRD queda en español como la PROPUESTA.
8. **Neutral de plataforma donde no cuesta nada:** `ChatResponder` y el protocolo hablan de "comando", "canal" y
   "directo", no de Kick. Solo `KickConnector` y el Worker conocen Kick.

---

## 5. Especificación

### 5.1 Mazo activo (K1)

**Contrato** (`DustPlanner.Contracts.ActiveDeckExport`, `schema_version: 1`) en `inbox/<account_key>/active-deck.json`,
con escritura atómica como los demás:

```json
{
  "schema_version": 1,
  "exported_at": "2026-10-18T20:31:05+02:00",
  "hdt_version": "1.59.2",
  "account": { "hi": 0, "lo": 0, "battletag": "Tsukuru#1234", "region": "EU" },
  "reason": "game_start",
  "deck": {
    "name": "Priest Control",
    "deckstring": "AAECAa0G…",
    "hdt_deck_id": "6f0c…",
    "card_class": "PRIEST",
    "format": "standard"
  }
}
```

- `deck` es `null` si no hay mazo activo o si es de Arena.
- `reason` puede ser `game_start`, `mode_change`, `deck_selected` o `manual`.
- **Cuándo se escribe:**
  - Al empezar una partida (`GameEvents.OnGameStart`).
  - En los cambios de modo que ya exportan.
  - Cuando HDT cambia de mazo activo, si existe un evento público para eso. Se verifica en K0 y se suma a
    `required-apis.json`. Si no existe, alcanza con el inicio de partida.
- Se usa el *throttle* del exportador y no se reescribe si el contenido no cambió (hash).
- **Servicio:** `OverlayState.ActiveDeck { HdtDeckId, Name, Deckstring, CardClass, Format, UpdatedAt }`, leído por fecha de
  modificación como `rank.json`.
- **Base:** cada exportación se guarda en `active_deck` con su `hdt_deck_id`. Al registrar una partida de
  `Power.log`, se busca el mazo activo exportado entre 10 min antes y 60 s después del inicio y de la misma clase. Si
  existe, `game.deck_id` apunta a la fila de la tabla `deck` con `origin = 'hdt'` y `external_id = hdt_deck_id`, y se
  crea la fila si no existe. Así se gana winrate por mazo sin nada extra. La tolerancia de 60 s cubre que la
  exportación se escribe en `OnGameStart`, casi al mismo tiempo que la partida.
- **Overlay:** una celda nueva `deck` (nombre del mazo), ocultable con `?hide=deck`.

### 5.2 Comandos y textos (K2)

`GET /api/chat/{comando}?lang=es|en&user=<nombre>` devuelve `text/plain; charset=utf-8`, una línea de 500
caracteres como máximo. Si falta un dato, responde con un texto amable, no con un error (por ejemplo, "Todavía no hay
mazo activo"). Otros casos:

- `404` si el comando no existe o está desactivado.
- `204 No Content` si está en cooldown. La guía de Streamer.bot indica no publicar respuestas vacías.
- `user` es opcional. Sin él, solo se aplica el cooldown global.
- Ninguna respuesta empieza con el prefijo, para que un bot nunca dispare otro comando.

| Comando | Alias por defecto | Ejemplo (es) |
|---|---|---|
| `deck` | `mazo` | `Priest Control · AAECAa0G…` (con la página pública activa: `Priest Control · lista: https://live.dustplanner.app/d/tsukuru-tv · AAECAa0G…`; el código va siempre al final — el que lo pide ya sabe qué hacer con él — y el slug lo conoce el host por el `hello` del Worker) |
| `rank` | `rango` | `Diamante 3 · Standard` / `Leyenda #1.234` |
| `wr` | `winrate` | `Hoy 7-3 (70 %) · sesión 4-1 · racha de 3 victorias` |
| `last` | `ultima` | `Última: victoria contra Mage en 9 turnos` |
| `dust` | `polvo` | `1.240 de polvo` (desactivado por defecto: el polvo es dato del streamer) |

- **Configuración** en `prefs.json`, bloque nuevo `chat`, validado como el resto y editable en *Settings*:
  `prefix` (`!`), comandos activos, alias, plantillas por idioma con marcadores (`{deck_name}`, `{deckstring}`,
  `{rank}`, `{today_w}`, `{today_l}`, `{today_wr}`, `{session_w}`, `{session_l}`, `{streak}`, `{last_result}`,
  `{opponent_class}`, `{turns}`, `{deck_url}`), cooldown global por comando (default 10 s) y por usuario (default 30 s).
- Las clases y resultados se traducen con el i18n del panel. El rango usa la etiqueta que ya arma `RankState.Label`.
- **Guía:** `docs/kick-streamerbot.md` explica cómo crear en Streamer.bot un comando por cada endpoint. Usa la
  subacción *Fetch URL* hacia `http://127.0.0.1:8765/api/chat/deck` y publica la respuesta en Kick. El puerto real
  está en `service.json`.

### 5.3 Worker (K3)

Rutas en `https://live.dustplanner.app`:

| Ruta | Qué hace |
|---|---|
| `GET /connect?pair=<hash>&port=<p>` | `pair` = SHA-256 de un `pair_verifier` aleatorio que el host guarda en memoria. Crea `state` + `code_verifier` (10 min, en el DO de pendientes) y redirige a `id.kick.com/oauth/authorize` con los cinco scopes |
| `GET /oauth/callback` | Valida `state` y canjea el código con el secreto. Lee el `user_id` (`/public/v1/users`) y el slug (`/public/v1/channels`), y descarta el email. Suscribe los eventos y **guarda los `subscription_id`**. Guarda los tokens cifrados en el DO del canal y actualiza el índice slug → `user_id`. Emite un código de emparejamiento de un solo uso (2 min) **atado al `pair`** y muestra una página intermedia que redirige a `http://127.0.0.1:<p>/kick/paired?code=…` (`p` solo puede ser 8765–8774) y deja el código a la vista para pegarlo a mano en el panel, por si el host ya no escucha en ese puerto |
| `POST /pair/exchange` | `{code, pair_verifier}` → `{device_token, channel:{user_id, slug, username}}`. Falla si `SHA-256(pair_verifier)` no coincide con el `pair` del código. El token se guarda hasheado (SHA-256) |
| `GET /ws` | `Authorization: Bearer <device_token>` → WebSocket con el DO del canal |
| `POST /webhooks/kick` | Verifica la firma con la clave pública cacheada. Si falla, vuelve a pedir la clave **como máximo una vez cada 5 min**; dentro de esa ventana responde `401` sin pedirla. Descarta mensajes de más de 10 min y deduplica por `Kick-Event-Message-Id` (24 h). Filtra con la configuración del canal en KV (§4, decisión 2) y enruta al DO por `broadcaster.user_id`. Actualiza el índice slug → `user_id` con `broadcaster.channel_slug`, porque el slug puede cambiar. Responde `200` siempre que la firma sea válida, aunque no haya dispositivo conectado, para que Kick no dé de baja la suscripción |
| `POST /disconnect` | Con el token de dispositivo: borra las suscripciones por sus `subscription_id`, revoca los tokens de Kick y borra el DO, la entrada de KV y el índice |
| `GET /d/<slug>` y `GET /api/d/<slug>/deck` | Página pública del mazo (K6). El prefijo `/d/` evita choques con las rutas propias. Resuelve el slug por el índice |
| `GET /health` | Estado y versión, sin datos de canales |

**Lista permitida (desde K3):** hasta K8, `/oauth/callback` solo acepta canales cuyo slug esté en la variable
`ALLOWED_SLUGS`. Los demás reciben una página de "beta cerrada".

**Renovación y reintentos:**
- El DO renueva el access token antes de que venza (`expires_in` − 5 min). La renovación se **serializa** dentro del
  DO, porque Kick rota el refresh token en cada renovación y dos renovaciones a la vez invalidan una de las dos.
- Si la renovación falla con `invalid_grant`, o si el token no tiene los scopes requeridos, el DO marca el canal con
  `needs_reconnect` y se lo avisa al cliente.
- `chat.send`:
  - Ante `401`, renueva y reintenta **una** vez.
  - Ante `429` o `5xx`, descarta el mensaje y manda un `error` al cliente. No hay cola.
- **Alarma diaria del DO:** hace `GET /public/v1/events/subscriptions` y vuelve a suscribir los eventos que falten.
  Hace lo mismo cada vez que se conecta un cliente.
- **Mientras el host está desconectado:** los comandos y canjes se descartan, coherente con no guardar chat. El DO
  guarda solo el último estado del directo (`is_live`, `title`, `started_at`, `ended_at`) y lo manda en el `hello`.

**Protocolo WebSocket** (JSON, campo `v: 1`):

| Dirección | `type` | Campos |
|---|---|---|
| Worker → servicio | `hello` | `channel{user_id, slug, username}`, `needs_reconnect`, `missing_scopes[]`, `live{is_live, title, started_at, ended_at}`, `server_version`, `subscriptions[]` (eventos de Kick con webhook; vacío si Kick los rechazó — relay ≥ 0.2.0) |
| Worker → servicio | `subscriptions` | `events[]`: el conjunto cambió después del `hello` (el Worker se vuelve a suscribir en cada conexión) |
| Worker → servicio | `chat.command` | `message_id`, `command`, `args`, `sender{user_id, username, badges[]}`, `at` |
| Worker → servicio | `stream.status` | `is_live`, `title`, `started_at`, `ended_at` |
| Worker → servicio | `reward.redemption` | `id`, `status`, `reward{id, title, cost}`, `redeemer{username}`, `user_input` |
| Worker → servicio | `error` | `code` (`kick_rate_limited`, `kick_unavailable`, `send_dropped`, `needs_reconnect`), `detail` |
| Servicio → Worker | `config` | `prefix`, `commands[]` (nombres + alias), `public_page`, `forward{stream_status, redemptions, redemption_filter[]}`. El Worker la replica en KV |
| Servicio → Worker | `chat.send` | `text`, `reply_to_message_id?` |
| Servicio → Worker | `deck.publish` | `name`, `deckstring`, `card_class`, `format` (solo si `public_page`) |
| Ambos | `ping` / `pong` | cada 30 s |

Después de cada `hello`, el servicio manda `config` y, si la página pública está activa, el `deck.publish` del mazo
actual.

**Seguridad:**
- El secreto de la app y la clave de cifrado viven solo como `wrangler secret`.
- Los logs del Worker nunca incluyen contenido de mensajes ni tokens.
- Límite de envío: como máximo un `chat.send` por segundo y por canal. Se descartan los que exceden y se avisa.
- Se ignoran los mensajes cuyo `sender.user_id` es el de la cuenta bot (variable `BOT_USER_ID` del Worker, que se
  carga en K0) o el del propio streamer cuando se responde con `type: user`. Además, `ChatResponder` garantiza que
  ninguna respuesta empiece con el prefijo.

### 5.4 Conector local (K4) y comandos nativos (K5)

- **Panel → Settings → Kick:**
  - Estado: desconectado, conectando, conectado como `@slug` o "hay que reconectar".
  - Botones **Conectar con Kick** y **Desconectar**.
  - Interruptores: comandos, página pública, resumen al cortar, aviso de predicción y canjes en el overlay.
- **Emparejar:**
  1. El host genera un `pair_verifier` aleatorio, lo guarda en memoria con 10 min de vida y abre en el navegador
     `…/connect?pair=<SHA-256 del verifier>&port=<puerto real>`.
  2. Recibe `/kick/paired?code=` en loopback y lo **rechaza si no hay un emparejamiento pendiente**. Canjea
     `{code, pair_verifier}` y guarda el token de dispositivo con DPAPI.
  3. Muestra "Listo, ya podés cerrar esta pestaña". Si el navegador no llega al host, Settings → Kick ofrece pegar el
     código a mano mientras siga pendiente.
- **Conexión:** reconexión con *backoff* exponencial (1 s a 60 s, con jitter). El estado se publica en `/events` para
  que el panel lo muestre.
- **Comandos nativos:** cada `chat.command` pasa por `ChatResponder`, igual que en K2, con los mismos cooldowns, y
  sale como `chat.send` respondiendo al mensaje original.
- **Convivencia con Streamer.bot:** los endpoints de K2 siguen activos. Si el conector está conectado, el panel avisa
  que hay que apagar los comandos duplicados en Streamer.bot.

### 5.5 Página pública del mazo (K6)

- Opt-in y apagada por defecto. Si está activa, el servicio manda `deck.publish` cada vez que cambia el mazo activo.
- **El Worker guarda solo** nombre, deckstring, clase, formato y fecha. **No guarda ni sirve datos de cartas.** La
  página decodifica el deckstring en el navegador del viewer y pide nombres y costos a HearthstoneJSON, como el resto
  del proyecto. Se respeta el §10.1.3 de la PROPUESTA.
- Muestra la lista por costo, el botón **Copiar código**, el nombre del canal con link a `kick.com/<slug>` y la hora de
  actualización. El estilo es el del sitio. Sin cookies ni analítica.

### 5.6 Eventos del directo (K7)

| Función | Disparador | Resultado | Default |
|---|---|---|---|
| Sesión de directo | `stream.status` con `is_live: true` y después `false` | Fila en `stream_session`. Las partidas del rango horario quedan asociadas | Siempre |
| Resumen al cortar | `is_live: false` | Mensaje de chat: `Directo terminado: 12-7 (63 %) · de Diamante 5 a Diamante 2` (récord por rango horario; rango con `rank_sample` antes y después) | Apagado |
| Aviso de predicción | Cambia `LastGame` mientras el canal está en vivo | Aviso privado por `/events`, mostrado en el panel y en la página `/status` que ya existe (toast + sonido opcional). Opcional, mensaje en el chat: `Terminó: victoria — ¡resolvé la predicción!` | Aviso privado encendido, chat apagado |
| Canjes en el overlay | `reward.redemption` con `pending` o `accepted` | Evento SSE `kick.redemption`, **una sola vez por `id` de canje** (pasar de `pending` a `accepted` no lo repite; el Worker deduplica por `id` 1 h y el host también). El overlay muestra 6 s un aviso con título y usuario. Filtro opcional por id de recompensa | Apagado |
| MCP | — | Tool `get_stream_sessions` (solo lectura): sesiones con récord, rango inicial/final y mazos usados | — |

Ningún canje dispara una acción en el juego ni en el sistema: solo se muestra.

---

## 6. Modelo de datos

**SQLite local** (migración nueva):

```
stream_session  id PK, account_key, platform('kick'), channel_slug, started_at, ended_at NULL,
                title, rank_start NULL, rank_end NULL, summary_posted_at NULL
active_deck     id PK, account_key, taken_at, hdt_deck_id NULL, name, deckstring, card_class, format, reason
                -- historial corto (30 días)
```

`game.deck_id` se completa desde `active_deck`. No hace falta tabla de mensajes ni de comandos: no se guarda chat.

**`prefs.json`** (bloques nuevos, con defaults y validación):

```json
{
  "chat": {
    "prefix": "!",
    "language": "es",
    "commands": {
      "deck": { "enabled": true, "aliases": ["mazo"], "template": null },
      "rank": { "enabled": true, "aliases": ["rango"], "template": null },
      "wr":   { "enabled": true, "aliases": ["winrate"], "template": null },
      "last": { "enabled": true, "aliases": ["ultima"], "template": null },
      "dust": { "enabled": false, "aliases": ["polvo"], "template": null }
    },
    "cooldown_seconds": 10,
    "user_cooldown_seconds": 30
  },
  "kick": {
    "public_page": false,
    "post_stream_summary": false,
    "prediction_reminder": "panel",
    "show_redemptions": false,
    "redemption_filter": []
  }
}
```

`template: null` usa el texto incluido para ese idioma. `prediction_reminder` admite `off`, `panel` o `panel+chat`. **Worker (SQLite del DO por canal):** `channel`,
`kick_tokens` (cifrados, con `scope` y vencimiento), `subscription` (`subscription_id`, evento, versión), `device`
(hash, creado, último uso), `live_state`, `public_deck`, `processed_message` y `processed_redemption` (id + vence).
**Workers KV:** `config:<broadcaster_user_id>` (lo que manda `config`) y `slug:<slug>` → `broadcaster_user_id`.

---

## 7. Riesgos

| # | Riesgo | Prob. | Impacto | Mitigación |
|---|---|---|---|---|
| R1 | La API de Kick cambia: es joven y su changelog muestra deprecaciones cada pocos meses | Alta | Medio | Todo lo de Kick encerrado en el Worker y en `KickConnector`. Tests con los payloads documentados. Versión de evento explícita |
| R2 | Kick da de baja la suscripción si el Worker falla más de un día | Media | Alto | El Worker responde `200` con firma válida aunque no haya dispositivo. `/health` monitoreado. Al conectarse, el servicio pide verificar y re-suscribir |
| R3 | Webhooks con demora en hora pico (lo reportan los autores de ChatRD/Streamer.bot) | Media | Bajo | Los comandos toleran segundos de demora. Se descartan eventos de más de 10 min |
| R4 | Filtración del secreto o de tokens | Baja | Muy alto | Secretos solo en `wrangler secret`, tokens cifrados, tokens de dispositivo hasheados, revocación en `/disconnect`, nada de tokens de Kick en la PC |
| R5 | Privacidad del chat de terceros | Media | Medio | No se guarda chat. Se reenvían solo comandos declarados. Logs sin contenido. Página de privacidad en el sitio |
| R6 | Límites del plan gratis de Cloudflare: cada mensaje de chat de un canal conectado es una petición al Worker | Media (si crece) | Medio | Filtrar antes de tocar el DO. Medir en la beta. Plan pago como salida. Pedir verificación de la app al pasar los 1.000 canales |
| R7 | Spam del bot o abuso de comandos | Media | Bajo | Cooldowns global y por usuario. Un envío por segundo por canal. Comandos desactivables |
| R8 | HDT cambia `ActiveDeck` o el evento de inicio de partida | Media | Bajo | Sigue detrás de `IHdtGateway` con detección de capacidades. `HdtApiCheck` lo cubre |
| R9 | ToS de Blizzard | Baja | Muy alto | Sin cambios: el evolutivo no lee nada nuevo del cliente ni automatiza nada |
| R10 | Términos de desarrollador de Kick (bots, datos) | Baja | Alto | Revisarlos antes de publicar la beta (Q1). Pedir verificación de la app |

---

## 8. Preguntas abiertas (respondelas antes de K3)

- **Q1.** ¿Revisaste los *Developer Terms* de Kick para una app que publica en chats de terceros y guarda tokens?
- **Q2.** Subdominio del Worker: ¿`live.dustplanner.app` está bien?
- **Q3.** ¿Las respuestas salen como **bot** de la app (`type: bot`, con nombre propio, por ejemplo *DustPlannerBot*)
  o como el propio streamer (`type: user`)? Recomendado: bot.
- **Q4.** Comandos y alias por defecto: ¿`!deck`, `!rank`, `!wr`, `!last` con alias en español? ¿`!dust` apagado?
- **Q5.** Página pública: ¿opt-in y apagada por defecto, como propone el PRD?
- **Q6.** Aviso de predicción: ¿alcanza con el aviso privado o querés también el mensaje en el chat?
- **Q7.** ¿Querés el camino nativo para tu canal antes de abrirlo a otros, con una beta privada por lista de slugs
  permitidos en el Worker?
- **Q8.** ¿Twitch entra en el horizonte? Si la respuesta es no, la neutralidad del §4.8 queda como está y no se
  invierte más en abstracciones.

---

## 9. Plan de implementación por etapas

Tamaños como en la PROPUESTA (S ≈ 1–2 días, M ≈ 3–5, L ≈ 1–2 semanas, a tiempo parcial). **Humano** = algo que solo
puede hacer el mantenedor; va en `VERIFICACION.md`, sección K.

| Etapa | Entregable | Criterio de salida | Tamaño |
|---|---|---|---|
| **K0. Spike y prerequisitos** | Confirmar contra la OpenAPI de Kick los campos de §2.3–2.4 (suscripción y `broadcaster_user_id`, `chat`, qué cuenta firma con `type: bot`, largo máximo, límites) y qué scope exige el webhook de canjes. Buscar en HDT un evento público de cambio de mazo activo y sumar `OnGameStart` (y ese evento si existe) a `required-apis.json`. **Humano:** responder Q1–Q8; crear la app en kick.com/settings/developer **desde una cuenta dedicada al bot** (2FA, redirect `https://live.dustplanner.app/oauth/callback`, webhooks a `https://live.dustplanner.app/webhooks/kick`); anotar el `user_id` de la cuenta bot; activar Workers y KV en Cloudflare | Respuestas y campos anotados en una sección nueva al final de este PRD ("11. Resultados de K0"). `HdtApiCheck` verde con las APIs nuevas | S |
| **K1. Mazo activo** | `ActiveDeckExport` en Contracts, exportación en el plugin, `ActiveDeck` en `OverlayState`, tabla `active_deck`, `game.deck_id`, celda `deck` en el overlay | Tests de contrato, de importación y de enlace con `game`. **Humano:** el nombre del mazo aparece en el overlay al empezar una partida | S |
| **K2. Textos para el chat** | Librería `DustPlanner.Kick` con `ChatResponder`, plantillas es/en, cooldowns, `GET /api/chat/{cmd}`, bloque `chat` en prefs y en Settings, `docs/kick-streamerbot.md` | Tests de cada comando con estado vacío, parcial y completo, largo máximo y cooldown. **Humano:** `!deck`, `!rank` y `!wr` responden en un directo vía Streamer.bot | S |
| **K3. Worker** | `cloud/kick-worker`: OAuth + PKCE, cifrado de tokens, renovación, webhooks con firma, deduplicación, filtro, configuración en KV, índice de slugs, DO con WebSocket con hibernación, emparejamiento atado al verifier, alarma de re-suscripción, lista permitida, `/disconnect`, `/health`. Workflow `worker.yml` que despliega con un tag `worker-v*` | vitest: firma válida/inválida/vieja con un par de claves de test, límite de re-pedido de la clave, idempotencia, filtro de comandos, renovación serializada, emparejamiento de un solo uso y con verifier incorrecto, re-suscripción, slug fuera de la lista, `/disconnect` borra todo. **Humano:** un mensaje real de tu canal llega a un cliente de prueba | L |
| **K4. Conector local** | `KickConnector` en el host, emparejamiento por loopback, token con DPAPI, reconexión, estado en `/events`, Settings → Kick | Tests contra un Worker falso en proceso: emparejar, reconectar, `needs_reconnect`. **Humano:** conectar y desconectar desde el panel | M |
| **K5. Comandos nativos** | `chat.command` → `ChatResponder` → `chat.send`, aviso de duplicados con Streamer.bot. En el Worker: envío a `POST /public/v1/chat` con límite de 1/s, reintento ante `401` y `error` ante `429`/`5xx` | Tests de extremo a extremo con el Worker falso y vitest del envío (con la API de Kick simulada). **Humano:** `!deck` responde con Streamer.bot cerrado | M |
| **K6. Página pública** | `deck.publish`, `/d/<slug>` y `/api/d/<slug>/deck` en el Worker, decodificación en el navegador, link en `!deck` | Tests del Worker y de la decodificación de deckstrings en JS (usando los vectores que ya genera `gen_deckstring_vectors.py`). **Humano:** la página muestra el mazo actual | M |
| **K7. Eventos del directo** | `stream_session`, resumen al cortar, aviso de predicción, canjes en el overlay, tool MCP `get_stream_sessions`. En el Worker: reenvío de `stream.status` y `reward.redemption` según `forward`, deduplicación por `id` de canje, `live_state` en el `hello` | Tests con eventos simulados y partidas en rango horario, y vitest del reenvío. **Humano:** un directo completo con resumen y aviso | M |
| **K8. Beta y publicación** | `docs/kick.md` (instalación, permisos, privacidad), página de privacidad en `site/`, sección en el README, abrir la lista permitida (vaciar `ALLOWED_SLUGS` o ampliarla), release 1.1.0 | **Humano:** otro streamer lo instala desde dustplanner.app y lo usa en un directo sin ayuda | S |

**Orden de dependencias:**

- K0 → K1 → K2. Acá ya sirve para tus directos.
- K0 → K3 → K4, en paralelo con K1–K2.
- K2 + K4 → K5.
- K1 + K5 → K6.
- K1 + K5 → K7.
- K6 + K7 → K8.

---

## 10. Reglas para quien implemente

1. Las del §10 de la PROPUESTA, sin excepción. Además: **nada de lo que llega de Kick ejecuta acciones fuera de
   DustPlanner.** Un comando o un canje solo produce texto o un aviso en pantalla.
2. Una entrada por etapa en `PROGRESO.md` (qué se hizo, qué queda, qué necesita al humano) y sus ítems en
   `VERIFICACION.md` §K.
3. Nunca commitear secretos: `.dev.vars` en `.gitignore`, `wrangler.toml` sin valores sensibles y un ejemplo con
   valores ficticios.
4. Ningún test pega contra Kick ni contra Cloudflare reales. Los payloads de test son los de la documentación oficial
   o sintéticos.
5. Todo dato de Kick que no se pueda confirmar en K0 se trata de forma conservadora: si no se sabe el largo máximo,
   se recorta a 500. Si no se sabe un límite de frecuencia, un mensaje por segundo.
6. La versión sube a 1.1.0 al cerrar K8. Las etapas intermedias pueden salir como 1.0.x si no rompen nada.

---

## Fuentes

- [KickEngineering/KickDevDocs](https://github.com/KickEngineering/KickDevDocs) (fuente de docs.kick.com, commit
  `54e18f5` del 05/10/2026):
  - [OAuth 2.1](https://github.com/KickEngineering/KickDevDocs/blob/main/getting-started/generating-tokens-oauth2-flow.md)
  - [Scopes](https://github.com/KickEngineering/KickDevDocs/blob/main/scopes/scopes.md)
  - [Webhooks y firma](https://github.com/KickEngineering/KickDevDocs/blob/main/events/webhook-security.md)
  - [Tipos de evento](https://github.com/KickEngineering/KickDevDocs/blob/main/events/event-types.md)
  - [Introducción a eventos](https://github.com/KickEngineering/KickDevDocs/blob/main/events/introduction.md)
  - [Chat](https://github.com/KickEngineering/KickDevDocs/blob/main/apis/chat.md)
  - [Recompensas de canal](https://github.com/KickEngineering/KickDevDocs/blob/main/apis/channel-rewards.md)
  - [FAQ (verificación de apps)](https://github.com/KickEngineering/KickDevDocs/blob/main/apis/faqs.md)
  - [Changelog](https://github.com/KickEngineering/KickDevDocs/blob/main/changelog.md)
  - [Creación de apps](https://github.com/KickEngineering/KickDevDocs/blob/main/getting-started/kick-apps-setup.md)
- [Bukk94/KickLib](https://github.com/Bukk94/KickLib): `Api/Chat.cs`, `Api/EventSubscriptions.cs` y los modelos de request
  (commit `b292e04` del 08/10/2026).
- [Cloudflare Durable Objects — precios y plan gratuito](https://developers.cloudflare.com/durable-objects/platform/pricing)
- [Kick Help: puntos de canal para streamers](https://help.kick.com/en/articles/11033027-guide-to-channel-points-for-streamers)
  · [Kick Help: predicciones para streamers](https://help.kick.com/en/articles/11182854-guide-to-predictions-for-streamers)
- [Streamer.bot: Kick](https://docs.streamer.bot/guide/platforms/kick) · [Streamer.bot: soporte de plataformas](https://docs.streamer.bot/faq/platform-support)
- Repo de DustPlanner: `docs/PROPUESTA.md`, `docs/PROGRESO.md`, `docs/panel.md`, `docs/overlay.md`,
  `docs/publishing.md`, `src/DustPlanner.HdtExporter/Hdt/HdtGateway.cs`, `src/DustPlanner.Host/OverlayState.cs`.

---

## 11. Resultados de K0 (17/10/2026)

### 11.1 Respuestas a las preguntas abiertas (§8)

| # | Decisión |
|---|---|
| Q1 | Developer Agreement revisado (ver §11.3). Dos ajustes de diseño salen de ahí; ninguno bloquea. |
| Q2 | `live.dustplanner.app`. Antes del primer deploy hay que comprobar que no exista un registro DNS `live` en la zona. |
| Q3 | Las respuestas salen como **bot** (`type: bot`). La app se crea desde una cuenta dedicada; nombre propuesto *DustPlannerBot* si está libre. |
| Q4 | `!deck`/`!mazo`, `!rank`/`!rango`, `!wr`/`!winrate`, `!last`/`!ultima` activos; `!dust`/`!polvo` apagado. |
| Q5 | Página pública opt-in, apagada por defecto. |
| Q6 | Se implementan `off`, `panel` y `panel+chat`; el default es `panel`. |
| Q7 | Beta privada por `ALLOWED_SLUGS` (`tsukuru-tv`: Kick escribe los slugs con guion; el relay acepta también `tsukuru_tv`) hasta el *go live*, que decide el mantenedor. |
| Q8 | Twitch fuera de alcance. La neutralidad del §4.8 queda como está; no se invierte en más abstracciones. |

### 11.2 Verificado contra la OpenAPI de Kick

Fuente: `https://api.kick.com/swagger/doc.yaml` (`info.version: 1.0.0`, leída el 17/10/2026). Resuelve los puntos
marcados **(OpenAPI)** en §2:

- **`POST /public/v1/chat`.** Campos: `content` (obligatorio), `type` (obligatorio, `user` | `bot`),
  `broadcaster_user_id` (opcional), `reply_to_message_id` (UUID, opcional). Límite de `content`: **500 caracteres
  percibidos (grapheme clusters) y como máximo 2.048 bytes en UTF-8**. `ChatResponder` recorta por ambos límites.
  Con `type: user`, `broadcaster_user_id` es obligatorio; con `type: bot` "no se requiere y se ignora: el mensaje
  siempre va al canal atado al token". **Qué cuenta aparece como remitente con `type: bot` no está en la
  especificación**; lo esperable sigue siendo la cuenta dueña de la app y se confirma en la prueba humana de K5.
  Scope: `chat:write`. Documenta `429`, sin cifras: rige la regla §10.5 (un mensaje por segundo y por canal).
- **`POST /public/v1/events/subscriptions`.** Campos: `events[]` (`name`, `version`; obligatorio), `method`
  (`webhook`), `broadcaster_user_id` (opcional). Con token de usuario, `broadcaster_user_id` **se ignora y se infiere
  del token**; con token de app es obligatorio. Respuesta: `data[] { name, version, subscription_id, error }`.
  `GET` admite `broadcaster_user_id` opcional; `DELETE` recibe `id` (array de `subscription_id`). Límite: 10.000
  suscripciones por tipo de evento y app; 1.000 para `chat.message.sent` en apps sin verificar.
- **`GET /public/v1/channels`.** Sin parámetros devuelve **el canal del usuario autenticado** (scope
  `channel:read`). Campos: `broadcaster_user_id`, `slug`, `stream_title`, `stream { is_live, start_time, … }`.
- **`GET /public/v1/users`.** Sin `id` devuelve el usuario del token: `user_id`, `name`, `profile_picture`,
  `email`. El email se descarta.
- **`GET /public/v1/public-key`.** Sin autenticación: `data.public_key` (PEM).
- **Introspección.** `POST /oauth/token/introspect` → `data { active, client_id, exp, scope, token_type }`.
- **Scopes.** Los cinco de §2.2 existen con esos nombres exactos. El webhook `channel.reward.redemption.updated` no
  declara scope en la especificación ni en la tabla de eventos; los endpoints de recompensas exigen
  `channel:rewards:read`. Se sigue pidiendo ese scope (conservador).
- **Eventos.** Los campos de §2.3 coinciden con la tabla de eventos; además `broadcaster` y `sender` traen
  `channel_slug`, `is_verified`, `profile_picture`, y `channel.reward.redemption.updated` trae `redeemed_at` y
  `reward.description`. Cabeceras y firma como en §2.3 (RSA PKCS#1 v1.5 + SHA-256 sobre
  `"{message_id}.{timestamp}.{raw_body}"`, en base64). La clave se pide al endpoint y no se hardcodea.

### 11.3 Developer Agreement de Kick (Q1)

Fuente: `https://dev.kick.com/terms-of-service` (incorpora por referencia los Terms of Service y las Community
Guidelines; ley aplicable: Inglaterra y Gales, arbitraje ICC en Londres). Lo que afecta al diseño:

| Cláusula | Qué exige | Cómo cumple el PRD |
|---|---|---|
| Sched. 1 C (almacenamiento) | Contenido de Kick solo en caché ≤ 24 h, salvo autorización del usuario que controla los derechos | No se guarda chat. `processed_message` dura 24 h. `live_state` guarda solo `is_live/title/started_at/ended_at` del propio canal. `public_deck` es dato del streamer, no de Kick |
| §5.2, §8.4, Sched. 1 C (borrado) | Borrar todo dato de Kick y del usuario al terminar, al revocar o a pedido | `/disconnect` borra todo. **Ajuste K3:** si la renovación falla con `invalid_grant` (el usuario revocó desde Kick), el DO borra los tokens y programa el borrado del canal en 24 h, además de avisar `needs_reconnect` |
| Sched. 1 A, §13.2 (privacidad) | Política de privacidad pública: qué se recoge, uso, retención; consentimiento | `docs/kick.md` y página de privacidad en `site/` (K8). Sin cookies ni analítica en `/d/<slug>` |
| Sched. 1 D.2 (bots) | Nada de spam ni prácticas engañosas; respetar pedidos de bloqueo/baja | Cooldowns, 1 msg/s, comandos desactivables. El bot nunca inicia una conversación: solo responde a un comando que un viewer escribió o publica el resumen que el streamer activó. El streamer lo apaga desde Settings |
| §10.1 (endpoints) | Solo Program Materials documentados; nada de endpoints no documentados | Pusher descartado (§2.5). Solo API pública y webhooks |
| §3.6 (Keys, seguridad, rate limits) | Secretos en secreto; medidas de seguridad estándar; no eludir límites; reportar incidentes a Kick por mail | `wrangler secret`, tokens cifrados, tokens de dispositivo hasheados, 1 msg/s |
| §3.2, §10.1 (open source) | No combinar Program Materials con licencias que obliguen a divulgarlos; incluir avisos | MIT sobre código propio; en el repo no hay material de Kick. Se añade atribución a Kick en `docs/kick.md` |
| §6, Sched. 1 D.1 (atribución) | Camino claro a la fuente; marcas de Kick solo para atribuir | `/d/<slug>` enlaza a `kick.com/<slug>`; no se usa el logo de Kick salvo como atribución |
| §3.5 (aprobación) | Kick revisa y puede retirar la app a su sola discreción | Riesgo R10 sigue. Pedir verificación al pasar los 1.000 canales |

### 11.4 HDT

Comprobado con `HdtApiCheck` contra los binarios 1.55.6 y con el código fuente de HearthSim (rama principal):

- `DeckList.Instance.ActiveDeckChanged` es `public event Action<Deck?>`. Existe y se usa: la exportación del mazo
  activo corre en ese evento, en `GameEvents.OnGameStart` (`ActionList` sin payload), en los cambios de modo que ya
  exportan y en la exportación manual.
- Añadidos a `required-apis.json`: `DeckList.ActiveDeckChanged`, `GameEvents.OnGameStart`, `ActionList.Add(Action)`,
  `Deck.Class`, `Deck.Name` y `HearthDb.Deckstrings.Deck.Format`. Todo presente; `HdtApiCheck` verde.
- `meta.trigger` del mazo activo: `game_start`, `active_deck_changed`, `mode_changed:<MODO>` o `manual`.

### 11.5 Ajustes al PRD decididos en K0

1. **Envoltorio del contrato.** `active-deck.json` usa el mismo `meta` que los demás exports (`schema_version`,
   `kind: "active_deck"`, `exported_at`, `exporter_version`, `hdt_version`, `trigger`, `account`, `capabilities`) en
   vez de la forma plana del ejemplo de §5.1. `reason` pasa a ser `meta.trigger`. `deck.card_class` va en
   mayúsculas (`PRIEST`), igual que `game.player_class`, para que el enlace con las partidas sea una comparación
   directa. `deck.format` sale de `HearthDb.Deckstrings.Deck.Format` (`standard`, `wild`, `classic`, `twist`).
2. **Etiqueta del rango.** `RankState.Label` está solo en inglés (`Diamond 3 (5 stars)`). `ChatResponder` arma la
   etiqueta por idioma desde `LeagueId`/`StarLevel`/`LegendRank` (`Diamante 3` / `Leyenda #1.234`).
3. **Idioma del chat.** `chat.language` admite `null` (default): se resuelve con el idioma de la interfaz del sistema
   (`es` → español, cualquier otro → inglés). El idioma del panel vive en el navegador, así que no sirve como default.
4. **Versionado.** Corrección a la regla §10.6: cada entrega que suma funcionalidad sube el *minor*; los *patch*
   quedan para arreglos. **El número lo fija la release publicada en GitHub, no el PR**: mientras el tag `v1.1.0` no
   exista, todo lo que se mergea sigue siendo 1.1.0 (hoy K1–K5), y el minor sube recién con la primera entrega funcional
   posterior a esa release. Lo mismo para el relay con `worker-v*`. Decidido por el mantenedor el 18/10/2026.
5. **Pendiente humano de K0** (en `VERIFICACION.md` §K): crear la app en `kick.com/settings/developer` desde la
   cuenta bot con 2FA, redirect `https://live.dustplanner.app/oauth/callback` y webhooks a
   `https://live.dustplanner.app/webhooks/kick`; anotar `client_id` y el `user_id` de la cuenta bot (el
   `client_secret` solo en `wrangler secret`); activar Workers, KV y Durable Objects en Cloudflare; comprobar que no
   exista el registro DNS `live`. Nada de esto bloquea K1–K2.
6. **Resultado de la prueba humana de K5 (18/10/2026).** Con `type: bot` y el token del canal, el mensaje aparece en el
   chat como **`@DustPlanner` con insignia de bot** — el *bot de la app*, que lleva el nombre de la app, no la cuenta
   `dustplannerbot` que la creó — y como respuesta encadenada al mensaje del viewer. Dos requisitos de la app que no
   estaban escritos y costaron una tarde: *Webhooks* activados (si no, Kick acepta el OAuth pero rechaza todas las
   suscripciones y nada llega) y **el bot de la app creado** desde *Edit* en la página de la app (si no, `POST
   /public/v1/chat` responde `404`). Ambos están ahora en `VERIFICACION.md` K1 y en el README del relay, y el relay
   avisa de los dos: la tarjeta muestra qué suscripciones aceptó Kick y el motivo que da Kick al rechazar un mensaje.
7. **Página pública (K6).** El texto de `!deck` cambia a `{deck_name}{deck_link} · {deckstring}`: el código va
   siempre al final, sin la coletilla "copialo y pegalo" (quien lo pide sabe qué hacer con él). La página vive en
   `live.dustplanner.app/d/<slug>` con el slug como lo escribe Kick (`tsukuru-tv`; el guion bajo también resuelve),
   decodifica en el navegador con el mismo algoritmo que `DeckstringCodec` (verificado con los mismos vectores) y pide
   nombres y costos a HearthstoneJSON en el idioma del navegador. Sin cookies ni analítica; `noindex` mientras dure la
   beta. El Worker guarda el `deck.publish` solo mientras la configuración diga `public_page: true`.
