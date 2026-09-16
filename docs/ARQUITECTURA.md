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
- Tras el saludo, el servidor entrega un token UDP. El cliente envía `UdpBind` hasta recibir `UdpBindAck`; si UDP no funciona (firewall), los envíos `Unreliable` caen a TCP.
- `Ping`/`Pong` se responde en el propio hilo de red del servidor para no contar la cola del bucle principal como latencia.

Flujo de conexión:

```
Cliente                         Servidor
  ── HandshakeRequest ──────────▶  valida versión, nombre, contraseña, aforo
  ◀───────── HandshakeResponse ──  id, token UDP, nombre del servidor, MOTD
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
- Al aceptarse el saludo se crea una partida sandbox local (`saves/MultiKerbal`) en el UT del servidor. La partida local es una caché: si se pierde la conexión se vuelve al menú.
- Desactivado en la partida: guardado/carga rápida, revertir al lanzamiento y revertir al hangar.
- UI IMGUI: ventana de conexión en el menú principal y ventana de jugadores/chat desde la barra de aplicaciones. El chat bloquea los controles de KSP mientras se escribe y no interpreta texto enriquecido.

## Naves

**Autoridad.** Cada nave tiene como mucho un dueño, el único que envía su estado. Quien publica una nave nueva (lanzamiento, separación de etapas, EVA, bandera) pasa a ser su dueño; al pilotar una nave sin dueño se pide el control y, si lo tiene otro jugador, se vuelve a la nave anterior. Al desconectarse, las naves del jugador quedan libres.

**Mensajes.**

- `VesselProto` (TCP): nodo `VESSEL` de KSP más los nodos `KERBAL` de la tripulación, en texto comprimido con GZip. Se envía al crear la nave, al cambiar piezas, tripulación o nombre, y cada 30 s mientras está cargada. Lleva una versión de estructura que solo cambia en los tres primeros casos: los reenvíos periódicos actualizan los datos guardados sin obligar a los demás a recargar (y hacer parpadear) la nave. El servidor lo guarda sin interpretarlo en `Universe/Vessels/<id>.vessel`.
- `VesselUpdate` (UDP): elementos orbitales, latitud/longitud/altitud y rotación relativa al cuerpo; 10 Hz con física activa y cada 5 s sobre raíles. Se usan elementos orbitales porque no dependen del marco local de cada jugador (KSP lo cambia según la altitud) y se propagan solos hasta el UT de quien los recibe, lo que compensa la latencia.
- `VesselRemove`, `VesselOwnership` y `VesselOwnershipRequest`.

**Marionetas.** Las naves ajenas son naves reales de KSP (mapa, estación de seguimiento, fijar objetivo) pero siempre empaquetadas, sin física local. En vuelo KSP las coloca sobre la órbita recibida; las posadas se recolocan cada frame por latitud/longitud. Para que KSP no las destruya:

- Radio de desempaquetado 0 y de empaquetado infinito: KSP destruye las naves empaquetadas dentro de la atmósfera salvo que estén dentro de ese radio respecto a la nave activa.
- Se copian las marcas de posada/amerizada; si no, KSP cree que atraviesan el terreno y las hace estallar.
- Fuera del vuelo no se crean naves remotas que vuelan dentro de la atmósfera.
- Si aun así KSP destruye una marioneta, no se avisa al servidor: se vuelve a crear más tarde.

**Escenas.** KSP reconstruye las naves desde el guardado en cada escena con planetario (Centro Espacial, estación de seguimiento y vuelo). Al entrar se reconcilia: se adoptan las copias al día, se eliminan las desfasadas o borradas en el servidor y se crean las que falten.

**Otros detalles.** Antes de crear una nave se añaden al plantel los kerbals que falten y se comprueba que existan todas sus piezas (si no, aviso en pantalla en lugar de la ventana de error de KSP). Los asteroides no se generan en partidas multijugador: cada jugador tendría los suyos.

**Limitaciones conocidas de la fase 2.**

- Sin interacción física con naves ajenas: se atraviesan y no se puede acoplar (fase 3).
- Las naves posadas se mueven a saltos de 10 Hz y pueden verse desplazadas unos metros.
- La tripulación se comparte por nombre: dos jugadores pueden usar al mismo kerbal a la vez.
- Si KSP destruye una marioneta fuera del vuelo, marca a su tripulación como muerta en el plantel local.

## Hoja de ruta

### Fase 1 — Conexión, jugadores, chat y reloj ✅ (probada con dos jugadores)
### Fase 2 — Ver naves de otros jugadores ✅ (probada con dos jugadores)
Ver la sección [Naves](#naves). Comprobado en el juego: publicación al lanzar, aparición en la lista de la estación de seguimiento y en el mapa, seguimiento del vuelo, eliminación de escombros y ajustes de warp (aceptar, rechazar, aceptar por ausencia).

### Fase 3 — Interacción entre naves
- Colisiones: cada dueño simula su nave; las remotas se comportan como cuerpos cinemáticos con tolerancia a impactos elevada.
- Acoplamiento: lo resuelve el jugador que acopla; el servidor fusiona las naves, reasigna el control y el otro jugador pasa a pasajero hasta desacoplar.
- EVA junto a naves de otros, embarque y naves sin dueño simuladas por el jugador más cercano.

### Más adelante
- Comprobación de la lista de mods al conectar (imprescindible para instalaciones con mods).
- Progreso compartido (ciencia, fondos, tecnologías) si se juega en carrera.
