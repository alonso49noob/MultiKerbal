# Arquitectura de MultiKerbal

## Decisiones de base

| Tema | Decisión | Motivo |
|---|---|---|
| Tiempo | **Reloj compartido** (sin subespacios) | Un único presente para todos: mucho más simple de razonar, y es lo que permite acoplarse o chocar con otros jugadores sin conflictos temporales. |
| Autoridad | **Servidor dedicado .NET** | El universo existe aunque no haya nadie; se puede probar sin KSP. |
| Transporte | **TCP + UDP propio**, sin librerías | El Mono de KSP no trae `netstandard.dll` y LiteNetLib 2.x solo publica para `netstandard2.1`/`net8.0`. |
| Código compartido | `MultiKerbal.Common` multi-target `net472`/`net10.0` | El mismo `NetClient` lo usan el mod y los tests de integración. |

## Protocolo

```
TCP: [int32 longitud = 2 + payload][uint16 tipo][payload]   → mensajes fiables y ordenados
UDP: [uint64 token][uint16 tipo][payload]                   → estados que se reemplazan (≤ 1200 bytes)
```

- Todo en little-endian; textos UTF-8 con longitud; `ProtocolInfo.Version` se comprueba en el saludo.
- Cada mensaje es una clase con `Write`/`Read` registrada en `MessageRegistry` (los tests obligan a que cada tipo tenga una muestra de ida y vuelta).
- Tras el saludo, el servidor entrega un token UDP. El cliente envía `UdpBind` hasta recibir `UdpBindAck`; si UDP no funciona (firewall), los envíos `Unreliable` caen a TCP. En ese caso, cliente y servidor descartan los `Unreliable` mientras haya más de 32 KB por enviar en esa conexión: el siguiente estado llega enseguida, y encolarlos todos solo acumularía retraso.
- `Ping`/`Pong` se responde en el propio hilo de red del servidor para no contar la cola del bucle principal como latencia.

Flujo de conexión:

```
Cliente                         Servidor
  ── HandshakeRequest ──────────▶  valida versión, nombre, clave del servidor, cuenta, aforo
  ◀───────── HandshakeResponse ──  id, token UDP, nombre del servidor, MOTD, cuenta nueva, tipo de partida
  ◀──────────────── PlayerList ──
  ◀──────────────── TimeState ──   UT + ServerTime + Rate
  ── UdpBind (UDP) ────────────▶
  ◀──────────── UdpBindAck (UDP)
  ── Ping cada 2 s ────────────▶
```

## Servidor

- **Un solo hilo de lógica** (`ServerHost`, 50 Hz). La E/S es asíncrona (`NetworkServer`) y solo encola `ServerEvent`; los sistemas no necesitan cerrojos.
- Expulsión ordenada: se envía `Disconnect` con motivo, se vacía la cola y se cierra con FIN.
- Persistencia en `Universe/universe.json` con escritura atómica; autoguardado cada 60 s y al vaciarse el servidor.

**Cuentas** (`Persistence/AccountStore.cs`, `Universe/accounts.json`). Cada jugador entra con su nombre y su contraseña de jugador, aparte de la clave del servidor (que es la misma para todos). La primera vez que entra un nombre, queda registrado con esa contraseña (mínimo 4 caracteres); después solo entra quien la sepa. Se guarda como hash PBKDF2-SHA256 con sal (100 000 iteraciones), así que ni el administrador puede leerla: si alguien la olvida, `account delete <jugador>` borra la cuenta y el siguiente que entre con ese nombre elige contraseña nueva. Los nombres no distinguen mayúsculas y se usa el que se registró. Como el dueño de una nave es un nombre, la cuenta es lo que protege las naves de cada uno.

- Limitación: la contraseña viaja sin cifrar dentro del saludo (como la clave del servidor). En el cliente, "Recordarla" la guarda en texto plano en `PluginData/MultiKerbal/settings.cfg`.

## Reloj compartido

Servidor (`TimeSystem`): `UT(t) = UT_base + (t − t_base) × rate`, rebasado en cada cambio de warp. Se pausa cuando no hay jugadores (configurable).

Consenso (`WarpConsensus`): de los jugadores que *participan* (Centro Espacial, estación de seguimiento, vuelo), el warp efectivo es el **mínimo** pedido. Warp sobre raíles y warp físico no se mezclan: si hay de ambos, x1.

Ajustes de warp: cada jugador puede **aceptar automáticamente** el warp sobre raíles de los demás hasta un máximo (`WarpRequest.AcceptUpTo`) o **rechazarlo automáticamente** (`WarpRequest.AutoDeny`). Para el consenso, quien acepta cuenta como si pidiera `min(AcceptUpTo, máximo pedido)`: acompaña sin adelantarse a nadie y, si nadie pide warp, no lo inicia. Las condiciones (pilotando, en la atmósfera, motores encendidos, naves cerca, límites de altitud de KSP) dependen de la nave de cada uno, así que se evalúan en el cliente (`WarpPolicyEvaluator`) y el servidor solo recibe el resultado. También se puede aceptar por ausencia: `IdleTracker` mide el tiempo sin teclado ni ratón (KSP en segundo plano tampoco recibe entradas, así que cuenta como ausencia) y, superado el umbral, se acepta sin mirar el resto de condiciones. El rechazo manda: con `AutoDeny` activado nunca se acepta automáticamente (si sus condiciones no se cumplen, el jugador decide), y un rechazo sin condiciones cuenta también en escenas sin tiempo, como el hangar. `TimeState.LimiterAutoDenies` permite decir a quien pide warp que el otro lo rechaza automáticamente. Si KSP o el jugador bajan un warp aceptado, lo aceptado queda limitado a ese nivel hasta que nadie pida warp.

Cliente:

- `ClockSync` estima el reloj del servidor con la muestra de menor RTT de los últimos 8 pings.
- `TimeSyncSystem` compara el UT local con el estimado: errores pequeños se corrigen gradualmente; los grandes (pausa, salir del hangar) saltan. Las naves con física no se empaquetan al saltar: KSP recalcula su órbita desde la física en cada frame, y empaquetar la nave activa en pleno vuelo interrumpía el control.
- `WarpDecision` separa lo que el jugador **quiere** (se envía) de lo que el consenso **permite** (se aplica). Si KSP baja el warp (atmósfera, cambio de SOI), la petición baja con él. A x1 siempre se pide "sobre raíles", porque KSP cambia de modo por su cuenta.
- Una petición de warp que nadie acompaña caduca a los 15 s (o al pulsar la tecla de bajar warp). Se avisa una sola vez a quien pide y una sola vez a quien está limitando a los demás.

## Cliente (plugin de KSP)

- `MultiKerbalAddon` (`KSPAddon.Startup.Instantly`, persistente) delega en `ClientCore`, que protege cada frame con try/catch.
- Al aceptarse el saludo se crea una partida local (`saves/MultiKerbal`) del tipo del servidor (sandbox, ciencia o carrera) en el UT del servidor. La partida local es una caché: si se pierde la conexión se vuelve al menú.
- Desactivado en la partida: guardado/carga rápida, revertir al lanzamiento y revertir al hangar.
- UI IMGUI: ventana de conexión en el menú principal y ventana de jugadores/chat desde la barra de aplicaciones. El chat bloquea los controles de KSP mientras se escribe y no interpreta texto enriquecido.

## Naves

**Autoridad: piloto y dueño.** Son dos cosas distintas (reglas en `Common/Vessels/VesselAccess.cs`, comprobadas en el servidor y reflejadas en el cliente):

- **Piloto** (control, solo en memoria): como mucho uno por nave, el único que envía su estado. Al pilotar una nave que nadie pilota se pide el control; si se deniega, se vuelve a la nave anterior (en la estación de seguimiento ni siquiera se deja pulsar "Volar"). El piloto la suelta en cuanto deja de simularla: ni es su nave activa ni está cargada cerca, y va en órbita, posada o amerizada (antes envía su definición y su estado finales). Las que siguen en vuelo atmosférico o suborbital se quedan con él hasta que KSP las destruya. Al desconectarse, deja de pilotar todas.
- **Dueño** (se guarda en disco, por nombre de jugador, protegido por su cuenta): quien publica una nave nueva (lanzamiento, separación de etapas, EVA, bandera), con el acceso por defecto de sus ajustes. El acceso decide qué pueden hacer los demás: **privada** (nada), **compartida** (pilotarla; destruirla, recuperarla o borrarla no) o **pública** (todo). El dueño puede cambiar el acceso, regalarla a un jugador conectado o dejarla sin dueño; una nave sin dueño es de todos y cualquiera puede reclamarla. Hacerla privada saca a quien la estuviera pilotando. El administrador puede reasignarla con `owner`.
- **Préstamo** (`VesselLendRequest`, se guarda en disco): el dueño presta la nave a un jugador conectado, que puede pilotarla aunque sea privada. Si el dueño la está pilotando, además se la pasa. El dueño la recupera cuando quiere (si quien la tenía la está pilotando, deja de hacerlo) y quien la tiene puede devolverla. Regalarla termina el préstamo. Un kerbal en EVA es una nave más: se presta igual.
- **Destruir** necesita permiso del dueño (pública o sin dueño), aunque se esté pilotando. `VesselRemove` lleva el motivo: destruida, recuperada, borrada o fundida con otra. Fundirla (acoplamiento, garra, kerbal que embarca) la puede quien la pilota; el cliente lo distingue porque KSP avisa con `onPartCouple`/`onCrewBoardVessel` justo antes. Sin permiso, el servidor no la borra: quien la perdió deja de pilotarla y la recibe de vuelta. Si la destruyó, vuelve a como estaba cuando la tomó: al dar el control a alguien sin permiso para destruirla, el servidor guarda un punto de restauración (definición, tripulación y estado) y, si la destruye, la restaura y sube su versión de estructura para que todos la recarguen.
- **Kerbals**: un kerbal no puede ir en naves de dos jugadores. `VesselProto` lleva los nombres de la tripulación y el servidor rechaza (`VesselReject`) una nave nueva con un kerbal que ya va en una nave que no pilota quien la publica; el cliente la quita de su partida (si es la que pilota, vuelve al Centro Espacial). Entre naves del mismo piloto sí se pueden mover (EVA, embarcar). En el cliente, los kerbals de las naves ajenas se marcan como ocupados en el plantel (no se ofrecen en el hangar), y al quitar una nave de la partida su tripulación queda libre en vez de darse por muerta.

**Mensajes.**

- `VesselProto` (TCP): nodo `VESSEL` de KSP más los nodos `KERBAL` de la tripulación, en texto comprimido con GZip. Se envía al crear la nave, al cambiar piezas, tripulación o nombre, y cada 30 s mientras está cargada. Lleva una versión de estructura que solo cambia en los tres primeros casos: los reenvíos periódicos actualizan los datos guardados sin obligar a los demás a recargar (y hacer parpadear) la nave. El servidor lo guarda sin interpretarlo en `Universe/Vessels/<id>.vessel`.
- `VesselUpdate` (UDP): elementos orbitales, latitud/longitud/altitud y rotación relativa al cuerpo; 30 Hz con física activa y cada 5 s sobre raíles (más de 30 no compensa: KSP simula la física a 50 Hz y el tráfico se doblaría). Se usan elementos orbitales porque no dependen del marco local de cada jugador (KSP lo cambia según la altitud) y se propagan solos hasta el UT de quien los recibe, lo que compensa la latencia.
- Suavizado (`Common/Vessels/PuppetMotion.cs`, con pruebas): quien recibe los estados no coloca la marioneta a saltos. Con los dos últimos calcula su velocidad y la adelanta hasta su UT, como hacen las órbitas (como mucho 0,25 s: si dejan de llegar estados, se para). Cuando llega uno nuevo, la diferencia con lo que se veía se absorbe en unos 0,3 s; si pasa de 50 m, se aplica de golpe y no cuenta como velocidad. En warp sobre raíles no se adelanta nada. Las posadas usan la posición y el giro suavizados; las que vuelan, solo el giro (la posición la pone KSP sobre la órbita).
- `VesselRemove` (con motivo) y `VesselReject`; `VesselControl` y `VesselControlRequest` (piloto); `VesselOwner`, `VesselOwnerRequest` y `VesselLendRequest` (dueño, acceso y préstamo). El archivo de cada nave guarda también dueño, acceso, préstamo, tripulación y punto de restauración (versión 4; las anteriores cargan sin préstamo ni tripulación, y las de antes de la 3, sin dueño).

**Marionetas.** Las naves ajenas son naves reales de KSP (mapa, estación de seguimiento, fijar objetivo) pero siempre empaquetadas, sin física local. En vuelo KSP las coloca sobre la órbita recibida; las posadas se recolocan cada frame por latitud/longitud. Para que KSP no las destruya:

- Radio de desempaquetado 0 y de empaquetado infinito: KSP destruye las naves empaquetadas dentro de la atmósfera salvo que estén dentro de ese radio respecto a la nave activa.
- Se copian las marcas de posada/amerizada; si no, KSP cree que atraviesan el terreno y las hace estallar.
- Fuera del vuelo no se crean naves remotas que vuelan dentro de la atmósfera.
- Si aun así KSP destruye una marioneta, no se avisa al servidor: se vuelve a crear más tarde.

**Escenas.** KSP reconstruye las naves desde el guardado en cada escena con planetario (Centro Espacial, estación de seguimiento y vuelo). Al entrar se reconcilia: se adoptan las copias al día, se eliminan las desfasadas o borradas en el servidor y se crean las que falten.

**Otros detalles.** Antes de crear una nave se añaden al plantel los kerbals que falten y se comprueba que existan todas sus piezas (si no, aviso en pantalla en lugar de la ventana de error de KSP). Los asteroides no se generan en partidas multijugador: cada jugador tendría los suyos (los pocos que KSP crea con la partida, antes de poder impedirlo, se eliminan). A las marionetas se les actualiza también la copia guardada (`protoVessel`: situación, posada, latitud…), porque con la nave lejos KSP consulta esa copia y no la nave.

**Limitaciones conocidas de la fase 2.**

- Sin interacción física con naves ajenas: se atraviesan y no se puede acoplar (fase 3).
- Las naves posadas que cambian de golpe de velocidad (un choque, un frenazo) pueden pasarse un poco antes de corregirse.
- Si KSP destruye una marioneta fuera del vuelo, marca a su tripulación como muerta en el plantel local hasta que la nave se vuelve a crear.
- Un kerbal que muere en la partida de un jugador sigue vivo para los demás (el plantel aún no se comparte).
- Perder piezas sin destruir la nave (un choque que arranca un ala) no necesita permiso: no se puede deshacer.

## Hoja de ruta

### Fase 1 — Conexión, jugadores, chat y reloj ✅ (probada con dos jugadores)
### Fase 2 — Ver naves de otros jugadores ✅ (probada con dos jugadores)
Ver la sección [Naves](#naves). Comprobado en el juego: publicación al lanzar, aparición en la lista de la estación de seguimiento y en el mapa, seguimiento del vuelo, eliminación de escombros y ajustes de warp (aceptar, rechazar, aceptar por ausencia).

### Fase 3 — Interacción entre naves ✅ (implementada, pendiente de probar a fondo)
En lugar de simular cada nave en su dueño y sincronizar los choques, **quien vuela cerca se queda con la física**: al acercarse a menos de 2,2 km a una nave que nadie pilota (y cuyo acceso lo permita), el cliente pide su control y deja de tratarla como marioneta. A partir de ahí KSP hace lo de siempre: chocar, acoplar, EVA. Al acoplarse, KSP funde las dos naves; el cliente publica la resultante y borra la otra, y el servidor se queda con una sola nave.

Cuando la nave sí la pilota otro jugador hay dos caminos, los dos con el servidor de árbitro:

- **Ceder el control** (`VesselHandoverRequest` → `VesselHandoverAsk` → `VesselHandoverGrant`): quien la pilota la suelta y el otro la toma. Es lo que hace falta para acoplarse con una nave que alguien está volando.
- **Copiloto** (`VesselCopilot`, `VesselInput`, `VesselAction`): quien pilota nombra copiloto a otro jugador. El copiloto lee sus propias teclas (`GameSettings`, ignorando los bloqueos de KSP), las manda a 20 Hz por UDP, y el cliente del piloto las suma a los suyos en `OnFlyByWire`. Las etapas y los grupos de acción van por TCP y solo si el piloto lo permite. La nave la sigue simulando una sola partida: la del piloto.

También se puede **mirar** cualquier nave ajena (botón "Mirar" en la lista de naves): se hace activa pero sigue siendo marioneta (empaquetada, movida por la red) y los mandos quedan bloqueados.

- Si está cargada cerca (a menos de 2,5 km), basta con cambiar la cámara.
- Si no, se carga el vuelo alrededor de ella, como "Volar" en la estación de seguimiento. Se parte de la partida actual con esa nave puesta al día (definición y último estado), porque fuera del vuelo puede que ni exista. Desde los primeros frames se mantiene empaquetada para que KSP no la simule aquí.
- Si quien la mira estaba pilotando, su nave tiene que estar estable (lo mismo que pide KSP para guardar): si no, se quedaría sin cargar en plena atmósfera.
- Al dejar de mirar se vuelve a la nave que se pilotaba, aunque esté lejos, o a la escena de la que se venía.
- Si la nave mirada cambia de estructura (etapas, acoplamiento), se vuelve a cargar el vuelo alrededor de la nueva: la nave activa no se puede quitar de la escena.
- KSP saca de raíles a la nave activa en cada `LateUpdate` sin mirar sus distancias de carga, así que mientras se mira se retiene cada frame con `OrbitPhysicsManager.HoldVesselUnpack`.
- Recargar el vuelo desde el vuelo no cambia `HighLogic.LoadedScene`: el cambio de escena se detecta también con `onGameSceneLoadRequested` y se espera a `onLevelWasLoaded`.
- Mientras se mira, el cambio de nave ([ ] o el mapa) está bloqueado, la nave propia no se suelta (para poder volver a ella) y los demás ven "Mirando ..." en la lista de jugadores. Para el warp cuenta como estar fuera del vuelo.

### Mods
Cada cliente manda al saludar una entrada por carpeta de `GameData` con la versión de sus DLL (`ModScanner`). El servidor no tiene KSP: guarda la lista del primer jugador en `Universe/mods.json` y compara las demás con ella (`ModCompare`, con pruebas). `ModPolicy` decide qué hacer: `off`, `warn` (avisa por el chat) o `strict` (rechaza). El motivo del rechazo y la lista van también en la respuesta al saludo, así que el jugador puede ver qué le falta aunque no le dejen entrar.

### Idiomas
Los textos van en el código en los dos idiomas (`Loc.T("es", "en")` en el cliente, `Lang.T` en lo compartido y el servidor), no en un archivo de claves: así no se queda ningún texto a medio traducir sin que se note al leerlo. El cliente elige por el ajuste del jugador o por el idioma de KSP; el servidor, por `Language` en `server.json`.

### Progreso compartido (base)
`GameMode` en `server.json`: `sandbox` (por defecto), `science` o `career` (con `StartingFunds`, 25 000 por defecto). El cliente crea su partida local del mismo tipo. El servidor (`Systems/ProgressSystem.cs`, `Universe/progress.json`) guarda fondos, ciencia, reputación y tecnologías investigadas:

- Cada cliente (`ProgressSync`) manda lo que cambia en su partida como **diferencia** (`ProgressChange`): así dos jugadores que gastan a la vez no se pisan. Las tecnologías van por su `techID`.
- El servidor suma, guarda y manda el total a todos (`ProgressState`), que lo aplican a su partida sin volver a mandarlo (los avisos de KSP mientras se aplica se ignoran). KSP vuelve a crear `Funding`, `ResearchAndDevelopment` y `Reputation` en cada escena, así que el total se reaplica al cambiar.
- El comando `progress` muestra el estado.

Falta: los experimentos ya hechos (la ciencia que da cada uno baja al repetirlo), las piezas compradas, los contratos, las mejoras de edificios y el plantel de kerbals (contratar, muertes).

### Más adelante
- Completar el progreso compartido (lo que falta arriba).
- Cifrar la conexión o, al menos, no mandar la contraseña de la cuenta tal cual.
