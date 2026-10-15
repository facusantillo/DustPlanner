# PROPUESTA — Herramienta de gestión de polvo para Hearthstone

> Nombre de trabajo: **DustPlanner** (provisional).
> Fecha del análisis: 15/10/2026. Alcance: solo análisis y diseño, sin implementación.

---

## 0. Resumen

- **Dust Utility** (CLJunge, GPL-3.0) era un plugin WPF para HDT. Leía colección y mazos en vivo con **HearthMirror** (lectura de memoria que viene con HDT) y los cacheaba en XML por cuenta. Elegía cartas para llegar a un monto de polvo con un **algoritmo greedy por rareza**, sin noción de "cuánto me duele". El último commit de código es de **mayo de 2020** (datos de Ashes of Outland), así que está roto en tres frentes: API de HearthMirror, sets hardcodeados y reglas de polvo. Además tenía un **auto-desencantador que hacía clics sobre la ventana del juego**. Eso no se reutiliza bajo ningún concepto.
- **HDT sigue vivo**: v1.59.2, commit del 13/10/2026. La colección se obtiene hoy con `Reflection.Client.GetFullCollection()`. HDT la expone por una clase pública (`CollectionHelpers.Hearthstone`) **pero no la guarda en disco**: la tiene en memoria y la sube a HSReplay.
- No encontré una herramienta mantenida que haga lo que pedís: optimizar qué desencantar por costo de oportunidad. Hay calculadoras de "cuánto me falta para este mazo" (OutOf.Cards, hsdecks, que está parado desde 2020). El Mass Disenchant del juego solo toca copias sobrantes.
- **Recomendación: enfoque híbrido (C)**. Un **plugin exportador mínimo** para HDT (~150 líneas) que solo vuelca colección y mazos a JSON. Al lado, un **núcleo standalone en .NET** (librería + CLI) con catálogo de cartas, deckstrings, colección, recomendador e historial. El núcleo no depende de HDT y es el que reutilizan después el tracker de `Power.log`, el servidor MCP y el overlay de OBS. La fragilidad frente a parches queda encerrada en un adaptador chico y reemplazable.

---

## 1. Cómo funcionaba Dust Utility

Lo cloné y revisé (`git log`: último commit 12/05/2020, *"Added data for Ashes of Outland"*). Son unas 12.400 líneas de C#, .NET Framework 4.7.2, WPF + MahApps.Metro + MvvmLight. Compila contra **copias binarias** de `HearthDb.dll`, `HearthMirror.dll` y `HearthstoneDeckTracker.exe` guardadas en `lib/`.

### 1.1 Arquitectura

| Capa | Archivos clave | Rol |
|---|---|---|
| Entrada del plugin | `DustUtilityPlugin.cs` (`IPlugin`) | Ciclo de vida, detección online/offline (`Core.Game.IsRunning`), suscripción a `GameEvents.OnModeChanged` |
| Cuentas | `AccountManagement/Account.cs`, `BackupManager.cs` | Cuenta = `BattleTag + Region`. Elige entre datos en vivo o caché. Backups diarios en zip |
| Lógica de cartas | `CardManagement/CardsManager.cs`, `SearchParameters.cs` | Filtros (rareza, clase, set, doradas, "solo no usadas") y selección por monto de polvo |
| Datos de Hearthstone | `Hearthstone/CardWrapper.cs`, `CardSets.cs`, `Extensions.cs` | Valor de polvo, sets conocidos, listas de no-crafteables |
| Offline / historial | `CardManagement/Offline/Cache.cs`, `HistoryManager.cs` | Snapshot de colección/mazos y diff para detectar cartas nuevas o desencantadas |
| Auto-desencantar | `CardManagement/AutoDisenchant/*` | **Mueve el mouse y hace clic en el cliente** (`User32.mouse_event`) para buscar y desencantar |
| UI | `UI/**` (≈60 % del código) | Ventanas, flyouts, drag & drop a una "selección" |

### 1.2 Cómo obtenía colección y mazos

- **Colección:** `HearthMirror.Reflection.GetCollection()`, filtrada a cartas presentes en `HearthDb.Cards.Collectible`. Cada carta era `{Id (string), Count, Premium (bool)}`.
- **Mazos:** `HearthMirror.Reflection.GetDecks()`, guardando solo `Type == 1` (constructed). El usuario tenía que entrar al menú *Play* al menos una vez para que el cliente tuviera los mazos cargados.
- **Disparador:** al cambiar de modo a `HUB`, `TOURNAMENT` o `COLLECTIONMANAGER` (más un intervalo configurable) guardaba todo en caché y calculaba el historial.
- **Datos de carta:** `HearthDb.Cards` (CardDefs embebido en la DLL) más tablas propias en `CardSets.cs`.

### 1.3 Cálculo de polvo y filtros

- **Valores** (`Extensions.GetDustValue`): tabla fija de desencantado. Normal/dorada: común 5/50, rara 20/100, épica 100/400, legendaria 400/1600. Devolvía 0 si el set era `CORE` o si la carta estaba en las listas `NonCraftable*`. **No contemplaba reembolsos por nerf**, ni firma/diamante, ni valores de crafteo.
- **"Solo no usadas"** (`CheckCollectionForUnusedCardsAsync`): para cada carta calcula el máximo de copias usadas en cualquier mazo no excluido, distinguiendo normal y dorada. Es candidata si se usa menos de 2 veces, si tenés más copias que las usadas y si no es una legendaria ya usada.
- **Modo "monto de polvo"** (`GetCardsForDustAmount`): recorre las rarezas en el orden que configuraste. Dentro de cada rareza ordena por polvo ascendente y va sumando hasta pasar el objetivo. Después, `RemoveRedundantCards` saca cartas de baja rareza si se pasó. Es **greedy puro**: no mide valor de la carta, uso en meta, clase ni formato.
- **Bug menor:** `CardWrapper.GetDustValue()` multiplica por el total de copias solo si la carta no está en ningún mazo. Con 3 copias y 1 en uso devuelve el valor de una sola copia.
- **Modo texto:** búsqueda por nombre, raza, mecánica, set o tipo.

### 1.4 Persistencia

- Carpeta `%AppData%\HearthstoneDeckTracker\DustUtility\Accounts\`. Un XML por cuenta y tipo: `{Nombre}_{Número}_{Región}_{collection|decks|history|prefs}.xml`, con `XmlSerializer`. `config.xml` global.
- **Historial:** diff entre la colección cacheada y la actual. Lo que bajó se considera desencantado y lo que subió, nuevo, con timestamp. El propio README admite que HDT a veces entregaba datos incorrectos y el historial quedaba con entradas falsas.
- **Backups:** zip diario de la carpeta de la cuenta.

### 1.5 Qué dependía de APIs que cambiaron

| Dependencia en Dust Utility | Estado hoy (HDT v1.59.2) | Impacto |
|---|---|---|
| `Reflection.GetCollection()` / `Reflection.GetDecks()` estáticos | Ahora `Reflection.Client.GetFullCollection()` / `Reflection.Client.GetDecks()`. La colección es un objeto con `Cards`, `Dust`, `CardBacks`, `PlayerRecords`, etc. | No compila |
| `Card.Premium` (bool) | `PremiumType` (0–3) + `TrialCount`. HDT lo agrupa como `[normal, dorada, tipo2, tipo3, trial×4]` por dbfId | Pierde diamante y firma |
| `lib/*.dll` congeladas en 2020 | HDT se distribuye con su HearthMirror, que cambia con cada parche del cliente | Choque de versiones al cargar |
| `CardSets.cs` hardcodeado hasta Outland | Desde 2020 salieron decenas de sets. El año actual es *Scarab* (rotación del 17/03/2026 según python-hearthstone) | Filtros y nombres de set vacíos |
| Supuesto "`CardSet.CORE` = sin polvo" | En 2021 el set Core se rehízo (nuevo `CORE`, dbf 1637). El básico viejo pasó a Legacy | La semántica cambió; hay que revalidar |
| Valores de polvo fijos | Siguen vigentes para normal y dorada, pero hoy existen reembolsos por nerf y premiums sin valor de desencantado | Recomendaciones incorrectas |
| `AutoDisenchant` (clics simulados) | — | **Riesgo de ToS. Se descarta** |
| MahApps.Metro / MvvmLight empaquetados | HDT usa MahApps 1.6.5 y Newtonsoft 13. MvvmLight está discontinuado | Conflictos de ensamblados dentro del proceso de HDT |

### 1.6 Qué se reutiliza y qué no

**Ideas que valen** (las reimplemento, no copio código):

1. Cachear colección y mazos por cuenta (clave = cuenta + región) para trabajar sin el juego abierto.
2. Snapshot en eventos de cambio de modo, con *throttling*.
3. Excluir mazos puntuales del cálculo de "en uso".
4. Historial por diff de snapshots. Lo mejoro: si el diff coincide con el plan que la herramienta te recomendó, la entrada gana confianza.
5. Resumen por set (progreso y polvo potencial, normal y dorado).
6. Lista explícita de no-crafteables como dato configurable, no como código.

**No se reutiliza:** la UI WPF, el greedy, las tablas de sets, el auto-desencantador ni las DLL congeladas.

**Licencia:** Dust Utility es GPL-3.0. Si copiás código, tu proyecto hereda la GPL. Reimplementar las ideas no la arrastra. Ver la pregunta abierta Q11.

---

## 2. Estado del ecosistema (octubre 2026)

### 2.1 HDT y su API de plugins

- **Mantenimiento:** activo. Último commit 13/10/2026 (*v1.59.2*). Compila con `net472`.
- **`IPlugin`** sigue igual de simple: `Name, Description, ButtonText, Author, Version, MenuItem, OnLoad, OnUnload, OnButtonPress, OnUpdate`. Eventos de juego en `Hearthstone_Deck_Tracker.API.GameEvents` (`OnModeChanged`, `OnGameStart/End/Won/Lost`, etc.).
- **Colección:** `Hearthstone_Deck_Tracker.Hearthstone.CollectionHelpers.Hearthstone` es pública. Tiene `GetCollection()` (async, cachea por cuenta `AccountHi/Lo`), `TryGetCollection(out ...)` y el evento `OnCollectionChanged`. El objeto `Collection` serializa a JSON con estas propiedades:
  - `collection`: `{dbfId: [n, g, p2, p3, trial_n, trial_g, trial_p2, trial_p3]}`
  - `dust`, `cardbacks`, `favorite_heroes`, `player_records`
  Es el mismo formato que HDT sube a HSReplay.
- **No persiste la colección en disco.** Solo guarda un hash para no resubirla. Para el enfoque standalone hace falta algo que la exporte. HDT sí guarda sus mazos en `%AppData%\HearthstoneDeckTracker\PlayerDecks.xml`.
- **Mazos del juego:** `Reflection.Client.GetDecks()`. HDT los usa para importar.
- **Rango:** HDT lo obtiene por memoria (`Reflection.Client.GetMedalInfo`), no del log. Relevante para el futuro tracker; ver Q8.
- **Logs:** HDT escribe `%LocalAppData%\Blizzard\Hearthstone\log.config` habilitando `Power` (verbose), `LoadingScreen`, `Arena`, `Achievements` y `FullScreenFX`, y lee la carpeta `Logs` de la instalación del juego. Con un `-uid` distinto de `hs_beta`, el config va en una subcarpeta con ese nombre.

### 2.2 Datos de cartas

| Fuente | Qué da | Ojo con |
|---|---|---|
| **HearthstoneJSON** — `https://api.hearthstonejson.com/v1/latest/{locale}/cards.collectible.json` | `id`, `dbfId`, `set`, `rarity`, `cardClass`, `collectible`, `cost`, `type`, textos localizados (incluye `esES`/`esMX`) | **No trae un campo Standard/Wild.** Datos © Blizzard. El sitio es CC0. No documenta caching. También publica `CardDefs.base.xml` |
| **HearthDb** (C#, MIT, `netstandard2.0`) | CardDefs embebido, `CardIds`, enums, `Deckstrings.DeckSerializer` (con sideboards) | Activo (v37.0.0, 13/10/2026). El CardDefs queda embebido al compilar, así que hay que actualizar el paquete en cada parche. **Su `Helper.WildSets` está desactualizado**: no incluye sets que rotaron en 2025–2026, como `TITANS`, `WILD_WEST` o `WHIZBANGS_WORKSHOP`. No encontré un paquete NuGet oficial (Q10) |
| **python-hearthstone** (MIT, v9.22.0 en git, 9.21.1 en PyPI del 23/09/2026) | Enums, `deckstrings`, `CRAFTING_COSTS`/`DISENCHANT_COSTS`, `STANDARD_SETS` por año y fechas de rotación | `STANDARD_SETS` se mantiene a mano. Año actual: `SCARAB` = `CORE, EVENT, EMERALD_DREAM, THE_LOST_CITY, TIME_TRAVEL, CATACLYSM, ESCAPEFROM_VIOLET_HOLD, BE` |

**Conclusión:** la legalidad Standard/Wild **no viene en ninguna fuente de cartas** y las dos librerías de referencia no coinciden entre sí. Propongo que sea un archivo de configuración propio (`format-rotation.json`), versionado y validado contra `python-hearthstone` en CI.

### 2.3 Deckstrings

Formato (spec de HearthSim): base64 de un buffer de *varints* sin signo.

```
0x00 | versión (=1) | formato (FormatType: 1=Wild, 2=Standard, 3=Classic…)
héroes:     n, dbfId×n            (normalmente 1)
cartas x1:  n, dbfId×n
cartas x2:  n, dbfId×n
cartas xN:  n, (dbfId, count)×n   (vacío en constructed)
[sideboards: bloque opcional (dbfId, count, ownerDbfId) — E.T.C., Zilliax, etc.]
```

- Cada sección va ordenada por dbfId ascendente (forma "canónica").
- Al pegar un mazo, las líneas `#` son comentarios y `### Nombre` da el nombre.
- La spec web no documenta los sideboards. HearthDb y la librería JS sí los soportan, así que conviene tomarlos de ahí como referencia.
- **Librerías:** C# `HearthDb.Deckstrings.DeckSerializer`; Python `hearthstone.deckstrings` (`Deck.from_deckstring`); JS `deckstrings`.
- **Propuesta:** escribir un decodificador propio (~80 líneas) para no atar el núcleo a la versión de HearthDb, con tests usando vectores generados por python-hearthstone.

### 2.4 Alternativas existentes

| Herramienta | Qué hace | ¿Resuelve tu caso? |
|---|---|---|
| Mass Disenchant del juego | Desencanta solo copias por encima del límite jugable | No: no mira mazos, meta ni objetivo |
| Búsqueda "Refund" en la colección | Lista cartas con reembolso completo vigente | Útil como **fuente manual** de la regla de nerfs |
| HSReplay.net (Untapped.gg) | Colección sincronizada por HDT y estadísticas de meta (parte premium) | No optimiza qué desencantar. Su JSON de colección es un endpoint interno y no documentado |
| OutOf.Cards (+ plugin HDT) | Sincroniza la colección y muestra el costo de mazos | Solo la parte de "cuánto me falta" |
| `hsdecks` (PyPI, MIT, último release 05/12/2020) | CLI: diff de mazos y cartas/polvo faltante importando colección desde HSReplay | Parado. Solo "faltantes" |
| Hearthstone Collection Tracker (plugin) | Seguimiento de sets y sobres con colección cargada a mano | No |
| Firestone (Overwolf) | **No verificado** en esta investigación | Revisar (Q12) |

No encontré nada mantenido que haga la parte 2: elegir qué desencantar minimizando el costo de oportunidad.

### 2.5 Reglas del juego relevantes al polvo

| Rareza | Craftear normal | Craftear dorada | Desencantar normal | Desencantar dorada |
|---|---|---|---|---|
| Común | 40 | 400 | 5 | 50 |
| Rara | 100 | 800 | 20 | 100 |
| Épica | 400 | 1600 | 100 | 400 |
| Legendaria | 1600 | 3200 | 400 | 1600 |

- **Nerfs:** una carta crafteable que recibe un cambio que no es un buff directo se desencanta por su **costo de crafteo completo** durante unas **dos semanas** tras el parche. Se encuentran escribiendo "Refund" en el buscador de la colección. Las cartas pasadas al Hall of Fame reembolsan al instante.
- **No crafteables:** cartas gratuitas (Core, legendarias regaladas, doradas del Rewards Track) y diamantes.
- **Firma:** según una guía no oficial, no se craftean ni se desencantan. Lo trato como "valor 0" hasta confirmarlo (Q4).
- **Límites:** 2 copias (1 si es legendaria), contadas por separado para normal y dorada.

---

## 3. Enfoques y comparación

La restricción es la misma para todos: **la herramienta nunca toca el cliente** (ni clics, ni teclas, ni inyección). Solo lee y recomienda; vos desencantás a mano.

### A) Plugin completo para HDT (C#, net472, WPF)
UI dentro de HDT, colección en vivo vía `CollectionHelpers`, persistencia propia.

### B) Standalone puro (CLI o web local) con import manual
La colección entra por un export: el JSON de HSReplay copiado desde el navegador, un CSV o una carga manual. No toca HDT.

### C) Híbrido: exportador mínimo en HDT + núcleo standalone (**recomendado**)
- El plugin solo escucha `OnCollectionChanged` y `OnModeChanged`, llama a `CollectionHelpers.Hearthstone.GetCollection()` y `Reflection.Client.GetDecks()`, y escribe `collection.json` y `decks.json` en una carpeta.
- El núcleo .NET hace todo lo demás y acepta también el import de HSReplay o CSV como *fallback*.

### D) Lector de memoria o log propio para la colección
Implementar mi propio HearthMirror. **Lo descarto:** es el máximo esfuerzo, la máxima fragilidad y el mayor riesgo de ToS. Además, la colección no aparece en los logs.

### Comparación

| Criterio | A) Plugin completo | B) Standalone + import manual | **C) Híbrido** |
|---|---|---|---|
| Esfuerzo MVP | Alto: WPF + depurar dentro de HDT | Bajo-medio | Medio (exportador chico + núcleo) |
| Fragilidad ante parches | **Alta**: cualquier cambio de HDT/HearthMirror rompe todo (es lo que mató a Dust Utility) | Media: depende de un formato no documentado de HSReplay o de cargar a mano | Baja-media: lo frágil son ~150 líneas reemplazables |
| Frescura de datos | En vivo | Manual, se desactualiza | Casi en vivo (se actualiza al pasar por menús) |
| Mantenimiento | Atado a releases de HDT, net472 y WPF viejo | Libre | Núcleo libre (.NET moderno). Plugin trivial |
| Reuso (tracker, MCP, OBS) | Malo: todo vive en el proceso de HDT | Bueno | **Bueno**: núcleo compartido |
| Riesgo ToS | El de usar HDT (lectura de memoria vía HearthMirror) | Mínimo (sin lectura del cliente) | El de usar HDT; **no agrega superficie** porque reutiliza lo que HDT ya lee |
| Dependencia de HDT abierto | Sí | No | Solo para refrescar. Funciona offline con el último export |

**Recomendación: C.** Las razones:

1. Aísla la parte que se rompe (lectura del cliente) en un componente mínimo, y la lógica valiosa (reglas y optimizador) queda en código testeable sin el juego.
2. Te deja en .NET moderno para el núcleo, el MCP y el overlay, en vez de net472 + WPF.
3. Tiene un *fallback* real si HDT cambia: el import de HSReplay o el CSV.
4. No suma riesgo de ToS respecto de lo que ya hacés usando HDT.

Si en el spike (etapa 0) el exportador resulta inviable, el plan cae a B sin perder nada del núcleo.

---

## 4. Arquitectura propuesta

```
                    ┌──────────────────────────────┐
  HDT (net472) ───► │ DustPlanner.HdtExporter      │  plugin mínimo: solo escribe JSON
                    └──────────────┬───────────────┘
                                   │ collection.json / decks.json  (%LocalAppData%\DustPlanner\inbox)
  HSReplay JSON / CSV ─────────────┤
                                   ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ Núcleo .NET (librerías)                                                     │
│  HsData        catálogo de cartas (HearthstoneJSON cacheado por build),     │
│                format-rotation.json, reglas de polvo, no-crafteables        │
│  Deckstrings   decode/encode propio (+ sideboards)                          │
│  Collection    modelo, importadores (ICollectionSource), snapshots, diffs   │
│  Planner       faltantes, scoring de "dolor", optimizador, explicaciones    │
│  Meta          IMetaSignalSource (MVP: archivo de códigos de mazo)          │
│  Store         SQLite (WAL) + migraciones                                   │
└───────┬──────────────────┬───────────────────┬──────────────────┬───────────┘
        │                  │                   │                  │
   CLI (MVP)        LogTracker (futuro)   MCP server (futuro)  Host HTTP local (futuro)
                    lee Power.log →       expone colección,    /api + /overlay (SSE)
                    partidas, winrate     historial, planes    → Browser Source de OBS
```

**Decisiones:**

- **Lenguaje:** C# en una versión LTS actual de .NET para el núcleo. Es tu stack, comparte modelos con el exportador y hay SDK de MCP para C#. Python queda como herramienta auxiliar para validar datos (scripts que comparan contra python-hearthstone).
- **Un solo dueño de los datos:** SQLite en `%LocalAppData%\DustPlanner\dustplanner.db`. Con WAL alcanza para varios lectores (MCP, overlay) y un escritor.
- **Catálogo por build:** descargo `cards.collectible.json` (y opcionalmente `CardDefs.base.xml`) y lo cacheo con su número de build. Un parche nuevo actualiza datos, no código.
- **Todo lo dependiente del parche vive en datos versionados:** sets por formato, no-crafteables, ventanas de reembolso y valores especiales (firma, diamante).
- **El exportador escribe un contrato estable:** su JSON es el de HDT (`collection` / `dust`) más un sobre con `schema_version`, cuenta, región, timestamp, build del cliente y versión de HDT. El núcleo valida el `schema_version`.

---

## 5. Especificación del MVP

### 5.1 Entradas

| Entrada | Forma | Obligatoria |
|---|---|---|
| Mazo objetivo | Deckstring (acepta el bloque pegado con `###` y `#`) | Sí |
| Colección | Último snapshot importado (exportador, HSReplay JSON o CSV) | Sí |
| Polvo actual | Campo `dust` del snapshot, o `--dust N` | Sí |
| Mazos guardados | `decks.json` del exportador y/o `--protect-deck <code>` | Recomendado (sin ellos, aviso fuerte) |
| Preferencias | `prefs.json`: clases que jugás, formato (Standard), política de doradas, lista blanca y negra de cartas | Con defaults |
| Señal de meta | `meta.txt` con códigos de mazo competitivos que pegás vos | Opcional |
| Reembolsos vigentes | `refunds.json` (dbfId, premium, vence) cargado a mano a partir de la búsqueda "Refund" | Opcional |

### 5.2 Salidas

1. **Faltantes:** carta, rareza, copias faltantes y costo de crafteo. Total de polvo necesario y diferencia con tu polvo actual.
2. **Plan de desencantado** (solo si falta polvo): lista de copias a desencantar con polvo, "dolor" y motivo. Totales, sobrante y antigüedad del snapshot.
3. **Excluidas relevantes:** las cartas valiosas que **no** propuso y por qué (protegida por mazo, por meta, etc.).
4. Formato humano por defecto, `--json` para que lo consuman el MCP y el overlay.
5. El plan se guarda como `DisenchantPlan` (estado `propuesto`). Cuando un snapshot posterior lo confirma, se registra en el historial.

### 5.3 Cálculo de faltantes

Para cada carta del mazo objetivo con cantidad `k`:
`tengo = normal + dorada + firma + diamante` (cualquier premium sirve para jugar).
`faltan = max(0, k − tengo)`. Costo = `faltan × craft_normal(rareza)`.

Las cartas no crafteables que falten (Core sin desbloquear, premios) se reportan aparte, como **"no se puede craftear"**, y no suman al polvo.

### 5.4 Reglas de recomendación

**Paso 1 — Copias protegidas (exclusión dura, nunca se proponen):**

1. **Copias en uso:** para cada carta, `usadas = max(copias en cada mazo guardado no excluido, copias en el mazo objetivo)`. Se protegen `usadas` copias jugables. Las faltantes del objetivo no cuentan, porque no las tenés. Qué premium se protege lo define la política de doradas (abajo).
2. Cartas en la lista blanca del usuario.
3. Cartas con desencantado 0 o desconocido: Core, no crafteables, firma y diamante (hasta confirmar valores).
4. **Legendarias de Standard con señal de meta** ≥ umbral (default: aparece en ≥ 10 % de los mazos de `meta.txt`, o en cualquier mazo de tu clase principal). Conservás 1 copia.
5. Si el snapshot de mazos tiene más de N días o no hay mazos cargados, **no se generan recomendaciones**. Solo faltantes, y se explica por qué.

**Paso 2 — "Dolor" de cada copia candidata** (0 a 100, con motivos visibles). Menos dolor = mejor candidata:

`dolor(copia) = base de la primera categoría que aplique (en el orden de la tabla) + modificadores`

| Categoría base (en orden de prioridad pedida) | Dolor base | Polvo que da |
|---|---|---|
| Reembolso por nerf vigente | 0 | **Costo de crafteo completo** |
| Copia sobrante (más allá de 2 o 1) | 0 | Normal (el Mass Disenchant ya lo resuelve; se informa) |
| Copia redundante: tenés la dorada o la de firma jugable y esta normal sobra | 1 | Normal |
| Dorada sin uso | 3 (Wild) / 10 (Standard) | Dorado |
| Normal solo Wild (si jugás solo Standard) | 2 (+3 si es legendaria) | Normal |
| Normal Standard, sin señal de meta | 25 | Normal |
| Normal Standard con señal de meta < umbral | 25 + 50 × señal | Normal |

| Modificador | Ajuste |
|---|---|
| Clase que no jugás | +5 (Wild) / +12 (Standard) |
| Set de Standard que rota en la próxima rotación | −3 (vale menos para vos) |

- La **política de doradas** es configurable. *conservar* (default) protege la dorada y deja la normal como redundante; *convertir en polvo* hace lo contrario. Ver Q5.
- Los pesos son un **punto de partida**: viven en `prefs.json` y la salida siempre muestra el desglose para que puedas calibrarlos.

**Paso 3 — Optimización:**

- Cada copia es un ítem `(polvo_i, dolor_i)`. Se busca el conjunto que cumpla `Σ polvo ≥ N` (N = polvo necesario − polvo actual) minimizando `Σ dolor + λ·sobrante + μ·cantidad_de_cartas`. Defaults: λ = 0,01 por punto de polvo sobrante y μ = 0,1 por carta, para desempatar a favor de planes más chicos y exactos.
- Es una **mochila de cobertura de costo mínimo**. Con polvo en múltiplos de 5, la programación dinámica sobre `N/5` estados (hasta ~10.000 para 50.000 de polvo) × unos pocos miles de copias se resuelve en milisegundos y da el **óptimo exacto**, no un greedy.
- Se reportan también **1 o 2 alternativas** (el mejor plan sin la carta más dolorosa elegida) para que puedas comparar.

### 5.5 Ejemplo de salida

Datos **ilustrativos** (nombres de carta ficticios). Faltan 1 legendaria (1600) + 2 épicas (800) + 1 rara (100) = 2500. Polvo actual 640, así que hay que conseguir **1860**.

```
DustPlanner · mazo objetivo: "Priest Control" (Standard)
Colección: snapshot 2026-10-15 13:52 (HDT exporter) · Mazos guardados: 9 (hace 2 h)

FALTANTES                                   copias   polvo
  Leyenda Sombría      Legendaria  Priest      1      1600
  Coro Abisal          Épica       Priest      2       800
  Vidente de Ceniza    Rara        Neutral     1       100
                                            ────────────────
  Total a craftear                                    2500
  Polvo actual                                         640
  A conseguir                                         1860

PLAN DE DESENCANTADO (óptimo · dolor total 53 · sobrante 0 · 14 copias)
  #  carta                        tipo     copias  polvo  dolor  motivo
  1  Guardián Errante (Épica)     normal     1      400     0   reembolso por nerf (vence 2026-10-24)
  2  Chispa Menor (Rara)          normal     2       40     2   redundante: tenés 2 doradas
  3  Rey Olvidado (Legendaria)    normal     1      400     5   solo Wild (legendaria)
  4  Golem de Brea (Épica)        normal     2      200     4   solo Wild
  5  Rata de Túnel (Común)        dorada     4      200    12   dorada sin uso · Wild
  6  Bruja del Pantano (Rara)     dorada     2      200     6   dorada sin uso · Wild
  7  Pacto Vil (Épica, Warlock)   dorada     1      400    22   dorada sin uso · Standard (10) + clase que no jugás (12)
  8  Cuervo Viejo (Rara)          normal     1       20     2   solo Wild
                                                   ─────
                                                    1860

NO PROPUESTAS (protegidas o más caras)
  Duelista Sombra (Legendaria Rogue, Standard)   400  dolor 37 → con ella en lugar de #7 el plan sumaría 68 vs 53
  Titán de Bronce (Legendaria neutral)          —    protegida: 34 % de mazos en meta.txt
  Oráculo del Alba (Épica Priest)               —    protegida: en uso en "Priest Aggro" (2 copias)

⚠ Recordá: la herramienta no desencanta. Hacelo a mano en la colección del juego.
```

---

## 6. Modelo de datos

SQLite. Las claves de carta son siempre **dbfId** (lo usan los deckstrings y el JSON de HDT). El `cardId` string es secundario.

```
card_def            (build_id, dbf_id) PK
  card_id, name_en, name_es, set_id, rarity, card_class, multi_class_ids,
  cost, type, collectible, craftable_normal, craftable_golden

card_build          build_id PK, source(hsjson|carddefs), fetched_at, locale_set

set_info            set_id PK, enum_name, name_es, released_at
set_format          (set_id, zodiac_year) PK, is_standard   -- desde format-rotation.json

dust_rule           (rarity, premium) PK, craft, disenchant   -- premium: 0=normal,1=dorada,2,3
refund_window       (dbf_id, premium) PK, starts_at, ends_at, note, source(manual|…)

account             account_key PK (hi:lo o battletag+region), display, region

collection_snapshot id PK, account_key, taken_at, source(hdt|hsreplay|csv), client_build,
                    dust, content_hash
collection_entry    (snapshot_id, dbf_id) PK, normal, golden, premium2, premium3

deck                id PK, account_key, origin(ingame|hdt|target|meta|manual), external_id,
                    name, hero_dbf_id, card_class, format, deckstring, captured_at, excluded
deck_card           (deck_id, dbf_id, sideboard_owner_dbf_id) PK, count

meta_source         id PK, name, captured_at, notes
meta_signal         (meta_source_id, dbf_id) PK, deck_share   -- 0..1

user_prefs          account_key PK, json   -- clases, formato, política doradas, pesos, listas

disenchant_plan     id PK, account_key, created_at, target_deck_id, need, total_dust,
                    total_pain, status(propuesto|confirmado|parcial|descartado), payload_json
disenchant_event    id PK, account_key, dbf_id, premium, count, dust_gained, detected_at,
                    from_snapshot, to_snapshot, plan_id NULL, confidence(alta|media|baja)
collection_event    id PK, account_key, dbf_id, premium, delta, detected_at, kind(nueva|crafteada|?)
```

**Tablas futuras** (reservadas para no migrar con dolor después):

```
game        id PK, account_key, started_at, ended_at, game_type, format, player_class,
            opponent_class, deck_id NULL, result(win|loss|tie), went_first,
            rank_before, rank_after, legend_rank NULL, source(powerlog)
rank_sample account_key, taken_at, format, league, star_level, stars, legend_rank
```

**Contrato del exportador** (`collection.json`):

```json
{
  "schema_version": 1,
  "exported_at": "2026-10-15T13:52:10+02:00",
  "hdt_version": "1.59.2",
  "client_build": null,
  "account": { "hi": 0, "lo": 0, "battletag": "Nombre#1234", "region": "EU" },
  "dust": 640,
  "collection": { "12345": [2, 0, 0, 0, 0, 0, 0, 0] }
}
```

---

## 7. Riesgos

| # | Riesgo | Prob. | Impacto | Mitigación |
|---|---|---|---|---|
| R1 | **Recomendación errónea que te hace desencantar algo útil** (irreversible) | Media | Alto | Exclusiones duras, snapshot viejo = sin plan, motivo visible por carta, se excluyen premiums con valor desconocido |
| R2 | HDT cambia `CollectionHelpers` o HearthMirror falla tras un parche | Alta (con el tiempo) | Medio | Exportador mínimo, *fallback* HSReplay/CSV, el núcleo nunca depende de HDT |
| R3 | Legalidad Standard o lista de sets desactualizada | Alta en cada rotación o expansión | Alto | `format-rotation.json` versionado + chequeo contra python-hearthstone + aviso si aparece un set desconocido |
| R4 | Valores de firma, diamante y reembolsos mal modelados | Media | Medio | Valor "desconocido" = excluido. Verificar en el juego (Q4) |
| R5 | **ToS de Blizzard** | Baja | Muy alto | Cero automatización de input (Blizzard ha baneado por automatizar). Sin lectura de memoria propia. Solo lo que ya lee HDT y archivos de log que el juego genera. Revisar el EULA vigente (Q1) |
| R6 | Fuente de meta: scraping prohibido o de pago | Media | Bajo | MVP con códigos pegados a mano. No scrapear (hsguru bloquea robots) |
| R7 | Endpoint de colección de HSReplay no documentado (cambia de formato, CSRF, auth) | Alta | Bajo | Solo *fallback*, con parser tolerante y versionado |
| R8 | Licencias (GPL de Dust Utility, HDT "todos los derechos reservados", datos © Blizzard) | Baja | Medio | No copiar código GPL. No redistribuir datos de cartas (descargarlos en runtime). Definir licencia (Q11) |
| R9 | Rango no disponible en `Power.log` | Media | Medio (tracker/overlay) | Validar en el spike del tracker (Q8). Alternativa: el exportador HDT también vuelca `GetMedalInfo` |

---

## 8. Preguntas abiertas (respondelas antes de implementar)

**Juego y reglas**

- **Q1.** ¿Revisaste el EULA/ToS vigente de Blizzard respecto de herramientas que leen memoria (vía HDT)? No verifiqué el texto actual. Solo encontré el antecedente de baneos por automatizar y que el handbook de torneos permite trackers.
- **Q2.** ¿El set Core aparece en el JSON de colección exportado y con qué conteos? (Afecta "faltantes" y la regla de no crafteables.)
- **Q3.** Reembolsos por nerf: ¿alcanza con cargarlos a mano desde la búsqueda "Refund", o querés automatizar la detección (parsear notas de parche)? ¿El reembolso de la dorada es su costo de crafteo dorado?
- **Q4.** Valores reales de desencantado de **firma** y **diamante**. Y el mapeo de los índices 2 y 3 del array de HDT: probablemente diamante y firma, pero hay que verificarlo con tu colección.
- **Q5.** Política de doradas por defecto: si tenés normal + dorada de una carta en uso, ¿conservás la dorada (cosmético) o preferís el polvo?

**Uso y preferencias**

- **Q6.** ¿Qué clases considerás "jugables" además de Priest? ¿Jugás algo de Wild que haya que proteger?
- **Q7.** ¿De dónde preferís sacar la señal de meta: códigos pegados de HSReplay, hsguru o Vicious Syndicate? ¿Tenés premium de HSReplay? Si querés automatizarlo, hay que revisar los términos de cada sitio.
- **Q8.** Para el tracker: ¿te sirve el rango que da HDT (memoria) a través del exportador, o tiene que salir solo de `Power.log`? Hay que validar qué expone el log hoy.
- **Q9.** ¿El MVP es solo CLI, o querés desde el inicio una página web local?

**Técnicas y legales**

- **Q10.** ¿Existe un paquete NuGet mantenido de HearthDb, o habría que usarlo como submódulo? No lo confirmé. Con el decodificador propio + HearthstoneJSON no hace falta para el núcleo.
- **Q11.** Licencia de tu proyecto: ¿MIT (sin copiar de Dust Utility) o GPL-3.0? ¿Lo vas a publicar como plugin de HDT para terceros?
- **Q12.** ¿Usás o querés evaluar Firestone u otra app que ya tenga colección y estadísticas? No la verifiqué.
- **Q13.** ¿Multi-cuenta o multi-región desde el día 1, o una sola cuenta?

---

## 9. Plan de implementación por etapas

Tamaños orientativos para una persona a tiempo parcial (S ≈ 1–2 días, M ≈ 3–5, L ≈ 1–2 semanas).

| Etapa | Entregable | Criterio de salida | Tamaño |
|---|---|---|---|
| **0. Spike** | Plugin HDT de prueba que vuelca la colección real a JSON. Verificar Q2 y Q4. Decidir Q1, Q5–Q7 y Q11 | JSON real de tu cuenta + respuestas anotadas | S |
| **1. Datos base** | `HsData` (descarga y caché de HearthstoneJSON por build, `format-rotation.json`, reglas de polvo) + `Deckstrings` con tests de vectores | Decodifica 20 mazos reales iguales a python-hearthstone. Clasifica Standard/Wild igual que `STANDARD_SETS` | M |
| **2. Faltantes (CLI)** | `dustplanner need <deckstring>` con import de JSON (HDT o HSReplay) | Coincide con lo que muestra el juego para 3 mazos | S |
| **3. Exportador HDT** | Plugin net472 mínimo: export en `OnCollectionChanged` / `OnModeChanged`, contrato con `schema_version` | Exporta solo al pasar por menús. No escribe nada más. No afecta el rendimiento de HDT | S |
| **4. Recomendador** | Exclusiones, scoring con motivos, optimizador DP, alternativas, `--json`, `meta.txt`, `refunds.json` | Tests de propiedad (nunca propone protegidas, siempre cumple N o explica por qué no) + revisión manual de 3 planes | M |
| **5. Persistencia e historial** | SQLite, snapshots, diff, matching contra plan, `dustplanner history` | Detecta un desencantado real después de seguir un plan | M |
| **6. Tracker de `Power.log`** | Asegurar `log.config` (sin pisar el de HDT), parser de partidas (inicio, fin, clases, resultado), tabla `game`, winrate | 20 partidas reales registradas igual que HDT | L |
| **7. Servidor MCP** | Tools/resources de solo lectura: `get_collection_summary`, `get_missing_for_deck`, `propose_disenchant_plan`, `get_history`, `get_winrate` | Se consulta desde Claude sin escribir en la base | M |
| **8. Overlay OBS** | Host HTTP local + página `/overlay` con SSE (rango, winrate de la sesión, polvo) para Browser Source | Se actualiza en menos de 5 s tras terminar una partida | M |

**Orden de dependencias:** 0 → 1 → 2 → (3 ∥ 4) → 5 → 6 → (7 ∥ 8). Las etapas 2 y 4 ya te dan valor aunque el exportador no exista: podés importar el JSON de HSReplay a mano.

---

## Fuentes

- Repositorio analizado: [CLJunge/Spawn.HDT.DustUtility](https://github.com/CLJunge/Spawn.HDT.DustUtility) (código revisado localmente).
- Código de [HearthSim/Hearthstone-Deck-Tracker](https://github.com/HearthSim/Hearthstone-Deck-Tracker), [HearthSim/HearthDb](https://github.com/HearthSim/HearthDb) y [HearthSim/python-hearthstone](https://github.com/HearthSim/python-hearthstone), revisados localmente (commits del 13/10/2026).
- [HearthstoneJSON — documentación de cartas](https://hearthstonejson.com/docs/cards.html)
- [Spec de deckstrings (HearthSim)](https://hearthsim.info/docs/deckstrings/) · [hearthstone-deckstrings (JS)](https://github.com/hearthsim/hearthstone-deckstrings)
- [python-hearthstone en PyPI](https://pypi.org/project/hearthstone/)
- [Crafting](https://hearthstone.wiki.gg/wiki/Crafting) y [Disenchant](https://hearthstone.wiki.gg/wiki/Disenchant) — Hearthstone Wiki
- [Búsqueda de cartas con reembolso — esports.gg](https://esports.gg/guides/hearthstone/how-to-search-for-nerfed-cards-in-your-hearthstone-collection-and-get-arcane-dust-refunds)
- [Signature cards — GosuNoob](https://www.gosunoob.com/guides/hearthstone-signature-cards/) (fuente no oficial)
- [Blizzard: acciones contra el botting (2014)](https://hearthstone.blizzard.com/en-us/news/16782216)
- [HearthSim: HDT en torneos oficiales](https://help.hearthsim.net/en/articles/4345072-can-i-use-hearthstone-deck-tracker-in-an-official-hearthstone-tournament)
- [hsdecks (PyPI)](https://pypi.org/project/hsdecks) · [Hearthstone Collection Tracker](https://github.com/riQQ/Hearthstone-Collection-Tracker) · [Import JSON de colección en OutOf.Cards](https://outof.games/realms/bugs/1mx/how-to-json-importer-for-hearthstone-collection/?page=1)
- [HSReplay.net](https://hsreplay.net/)

---

## 10. Decisiones y correcciones posteriores

Esta sección cierra las preguntas abiertas de la sección 8 y corrige la propuesta. Es la fuente de verdad: si algo de arriba contradice lo de abajo, manda lo de abajo.

### 10.1 Restricciones no negociables

1. La herramienta **nunca** interactúa con el cliente de Hearthstone: nada de clics, teclas, inyección ni lectura de memoria propia. Solo usa la API pública de plugins de HDT y archivos de log que el juego genera. Está explícito en el README y en `SECURITY.md`.
2. No se copia código de Spawn.HDT.DustUtility (GPL-3.0). Reimplementar ideas está bien.
3. No se redistribuyen datos de cartas de Blizzard en el repo: se descargan en runtime desde HearthstoneJSON y se cachean localmente. Los fixtures de tests usan subconjuntos mínimos o datos sintéticos.
4. Licencia MIT. README con disclaimer: *"Not affiliated with or endorsed by Blizzard Entertainment. Hearthstone is a trademark of Blizzard Entertainment."*
5. Ante cualquier dato del juego que no se pueda verificar (valores de polvo de premiums especiales, legalidad de sets), el comportamiento por defecto es conservador: ese ítem queda excluido de las recomendaciones de desencantado y se informa por qué.

### 10.2 Decisiones cerradas (resuelven las preguntas de la sección 8)

| Pregunta | Decisión |
|---|---|
| Q1 | Política de cero automatización y cero lectura de memoria propia, documentada en README y `SECURITY.md`. |
| Q2 | El código soporta que las cartas Core aparezcan o no en el export. Core se trata siempre como no crafteable y se reporta aparte en faltantes. |
| Q3 | Reembolsos vía `refunds.json` cargado a mano (`dbf_id`, `premium`, `starts_at`, `ends_at`, `note`). Se asume reembolso al costo de crafteo de su premium (dorada = crafteo dorado); queda marcado como **a verificar** en la documentación. |
| Q4 | Los índices 2 y 3 del array de colección de HDT **no se asumen**. El exportador incluye un modo diagnóstico que vuelca, para un puñado de cartas, el array crudo junto al nombre, para que el usuario confirme el mapeo. Hasta que exista una configuración confirmada, esas copias tienen valor desconocido y quedan excluidas. |
| Q5 | Política de doradas por defecto = conservar la dorada; la normal en uso pasa a redundante si existe dorada jugable. Configurable. |
| Q6 | Preferencias por defecto: formato Standard, clase principal Priest, no se juega Wild. Todo configurable en `prefs.json`. |
| Q7 | Señal de meta solo desde `meta.txt` con códigos de mazo pegados por el usuario. Prohibido el scraping. |
| Q8 | El rango sale del exportador de HDT (`GetMedalInfo` vía la API de HDT), no de `Power.log`. `Power.log` se usa para partidas, clases y resultados. |
| Q9 | MVP en CLI. La web local llega con el host del overlay (etapa 8). |
| Q10 | Decodificador de deckstrings propio en el núcleo. HearthDb solo en el exportador. |
| Q11 | MIT. El exportador se publica como plugin de HDT con releases en GitHub. |
| Q12 | Firestone fuera de alcance. |
| Q13 | Multi-cuenta y multi-región desde el día 1, con `account_key` en todo el modelo. |

### 10.3 Correcciones a la propuesta (obligatorias)

1. **Sobres pendientes.** Todo plan de desencantado empieza con el aviso: *"Abrí tus sobres pendientes antes de desencantar: la protección contra duplicados mira tu colección actual."* Si el snapshot tiene información de sobres sin abrir, se muestra la cantidad. (Verificado: ninguna API de HDT/HearthMirror expone esa cantidad hoy; el campo queda reservado como `unopened_packs = null`.)
2. **Rotación del mazo objetivo.** El comando de faltantes informa qué cartas del mazo objetivo pertenecen a sets que rotan en la próxima rotación, con la fecha y el polvo de crafteo en riesgo. Las fechas viven en `format-rotation.json`.
3. **Protección por meta.** La regla de protección por señal de meta aplica a legendarias **y** épicas de Standard. Umbral configurable, con default distinto por rareza.
4. **Sobrantes primero.** Cualquier copia por encima del límite jugable (2, o 1 si es legendaria), por premium, es siempre "copia sobrante" con dolor 0, antes que cualquier otra categoría. Hay un test con 4 doradas de una común: 2 son sobrantes, no "dorada sin uso".
5. **Contrato compartido.** El DTO del JSON del exportador vive en `DustPlanner.Contracts` (netstandard2.0), referenciado por el exportador (net472) y por el núcleo (.NET 10 LTS). Nada de modelos duplicados.
6. **Verificación de datos.** Antes de que el recomendador produzca planes, la etapa 1 incluye un chequeo automatizado que compara `format-rotation.json` contra `STANDARD_SETS` de python-hearthstone y falla en CI si difieren. No se confía en `Helper.WildSets` de HearthDb.

### 10.4 Decisiones de entorno tomadas al arrancar la implementación

- .NET 10 LTS para núcleo, CLI, MCP y host; exportador en net472; Contracts en netstandard2.0.
- Referencias de HDT para compilar el exportador: CI y desarrollo contra el zip del release **v1.55.6** (el último publicado en GitHub; el 1.59.x se distribuye por hsreplay.net). La carpeta `lib/hdt/` está ignorada por git y el script `tools/fetch-hdt-refs.ps1 -FromInstall` la llena con la instalación local. Cada API usada por el exportador se verificó contra 1.55.6 (binarios) y 1.59.2 (código fuente) y se aísla detrás de `IHdtGateway` con detección de capacidades en runtime. Resultado en `docs/VERIFICACION.md`.
- Código, identificadores, comentarios, mensajes de commit y documentación nueva en inglés. Este documento se conserva en español por ser el original.
