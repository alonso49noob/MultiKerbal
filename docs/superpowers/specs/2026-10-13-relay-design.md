# Relay de MultiKerbal: entrar con un código y lista pública de servidores

Fecha: 2026-10-13 · Estado: diseño aprobado por partes en conversación, pendiente de revisión del documento.

## 1. Objetivo

Que cualquiera pueda hospedar un servidor de MultiKerbal desde casa y que sus amigos entren **escribiendo un código corto**, sin tocar el router, sin instalar nada más y **aunque haya CGNAT**. Además, que los servidores que quieran aparezcan en una **lista pública** dentro del juego.

**Se considera conseguido cuando:** el autor hospeda desde su casa (su router no tiene UPnP) y un amigo de otra ciudad entra escribiendo el código, sin que ninguno de los dos instale nada aparte de KSP, el mod y el servidor.

**Decisiones del autor (tomadas en la conversación):**
- Habrá un ordenador central, el **relay**, en una máquina gratuita (Oracle Cloud Free Tier) que mantiene el autor.
- Se entra con **código** y también hay **lista pública**.
- Enfoque **B**: si el servidor es accesible directamente, los jugadores van directos; si no, por el relay.
- El servidor sigue siendo un programa aparte (decisión anterior que se mantiene).

**Fuera de alcance:** "hole punching", IPv6 en el relay, varios relays por regiones, cuentas de usuario, cifrado del tráfico, descripción larga o imágenes en la lista, lista de servidores recientes y servidor dentro de KSP.

## 2. Piezas

| Pieza | Proyecto | Qué cambia |
|---|---|---|
| **Relay** | `src/MultiKerbal.Relay` (nuevo, `net10.0`, consola) | Todo nuevo. |
| **Servidor** | `src/MultiKerbal.Server` | Se apunta al relay, acepta jugadores que llegan por él, contesta a pruebas y a consultas de estado. |
| **Mod** | `src/MultiKerbal.Client` | Conecta con código, ventana de lista pública, ventana Hospedar con estado, código dentro de la partida. |
| **Común** | `src/MultiKerbal.Common` | Mensajes del relay, códigos, plan de conexión en `NetClient`. Sigue siendo compatible con `net472`. |

El relay tendrá un nombre DNS fijo y gratuito, por ejemplo `multikerbal.duckdns.org`. El nombre definitivo se elige al montarlo (fase 5) y se fija como valor por defecto en `ProtocolInfo`. El puerto por defecto es **6760, TCP y UDP**.

## 3. Identidad y códigos

- **Clave del servidor:** la primera vez que arranca con el relay activado, el servidor crea 32 bytes aleatorios y los guarda en `<DataDirectory>/relay.key`. Quien tenga ese archivo es ese servidor.
- **Código:** el relay asigna a cada clave un código de 6 caracteres que se muestra como `XXX-XXX`, con un alfabeto sin caracteres confusos (`ABCDEFGHJKMNPQRSTUVWXYZ23456789`). El relay guarda la relación clave → código en `relay-data/codes.json`, así que el código es **permanente** y sobrevive a reinicios de los dos.
- **Escribir el código:** no se distinguen mayúsculas de minúsculas, y se ignoran espacios y guiones (`k7m q2p` = `K7M-Q2P`). En el campo "Servidor", algo con la forma de un código se trata como código, y todo lo demás como IP o nombre, como ahora.

## 4. Protocolo del relay

Usa el mismo formato de tramas que el juego (`FrameCodec`), con tipos de mensaje propios en un rango aparte. Todos los mensajes del relay llevan `RelayProtocolVersion` en el primero de cada conexión. Ningún mensaje del relay puede pasar de 64 KB.

### 4.1 Servidor ↔ relay

1. **Conexión de control (TCP, permanente).** El servidor manda `RelayRegister { clave, puerto de juego, versión del protocolo del juego, versión del mod, nombre, jugadores, máximo, tiene contraseña, público }`. El relay contesta con `RelayRegistered { código, secreto UDP }` o con `RelayRejected { motivo }` (versión vieja, clave baneada o demasiados servidores desde esa IP).
2. **Latido:** `RelayHeartbeat { nombre, jugadores, máximo, contraseña, público }` cada **15 s**. Si el relay no recibe nada en **45 s**, da el servidor por caído y lo quita de la lista.
3. **UDP:** cada **20 s** el servidor manda `RelayUdpHello { secreto UDP }` desde su **socket de juego** al puerto UDP del relay. Así el relay conoce por qué dirección UDP pública le llegan los paquetes y el router de casa deja pasar las respuestas.
4. **Reconexión:** si se pierde el control, el servidor reintenta a los 30 s y luego duplica la espera hasta un máximo de 5 min. Mientras tanto se sigue pudiendo entrar por IP.

### 4.2 Comprobación de acceso directo

- El relay la hace al registrarse el servidor, cada **10 min** y cuando el servidor manda `RelayRecheck`.
- **TCP:** abre una conexión a `IP pública del control : puerto de juego` y manda `RelayProbe { nonce }`. El servidor contesta `RelayProbeReply { nonce }` y cierra. Se da un plazo de 5 s.
- **UDP:** manda `RelayProbe { nonce }`, con ficha 0, a la misma dirección **desde un socket del relay distinto del de reenvío**, para que el agujero que abrió el "hola" no dé un falso positivo. El servidor contesta a quien se lo mandó. Se da un plazo de 5 s, con 3 intentos.
- El resultado se le comunica al servidor con `RelayReachability { tcp, udp }`. Solo cuenta como **"accesible directamente"** si funcionan **los dos**.

### 4.3 Jugador entra con código

1. **Consulta:** el mod abre una TCP con el relay y manda `RelayLookup { código }`. Recibe `RelayLookupResult { encontrado, nombre, versión del protocolo del juego, dirección directa o vacío }` y el relay cierra.
2. **Directo:** si hay dirección directa, el mod se conecta a ella con un plazo de **3 s**. Si lo consigue, sigue como una conexión por IP normal.
3. **Por el relay:** si no hay dirección directa o falla, el mod abre una TCP con el relay y manda `RelayJoin { código }`. El relay crea una sesión y manda por el control `RelayIncoming { sesión, secreto de la sesión, IP real del jugador }`. El servidor abre una **TCP nueva hacia el relay** y manda `RelayAttach { sesión, secreto }`. El relay manda `RelayJoinResult { ok }` al jugador y a partir de ahí **pasa los bytes tal cual** entre las dos conexiones. Si el servidor no se engancha en **10 s**, el jugador recibe `RelayJoinResult { error }`.
4. **En el servidor,** la conexión enganchada se trata como si se hubiera aceptado directamente (`NetworkServer.Adopt`), con la IP real del jugador en el registro y para `kick`.
5. **UDP por el relay:** cuando el servidor da la ficha UDP a un jugador que llegó por el relay, avisa con `RelayUdpToken { sesión, ficha }`. El mod manda su UDP al relay, a la misma dirección y puerto que la TCP. El relay lee solo los **8 primeros bytes** (la ficha):
   - Si el paquete viene de la dirección UDP registrada del servidor, lo reenvía a la última dirección conocida del jugador con esa ficha.
   - Si no, busca la ficha. Si es de una sesión viva, apunta la dirección del jugador y lo reenvía a la dirección UDP del servidor.
   - Si no hay ficha conocida, lo descarta.
   - Si el UDP no funciona, el mod pasa todo a TCP, como ahora.
6. Cuando cualquiera de las dos TCP de una sesión se cierra, el relay cierra la otra y olvida su ficha.

### 4.4 Lista pública

`RelayListRequest` → `RelayListResponse { servidores: código, nombre, jugadores, máximo, contraseña, versión del protocolo, versión del mod, directo }`. Solo incluye servidores con `público = true`, como máximo 500, ordenados por jugadores. Los nombres se cortan a 40 caracteres y se les quitan los caracteres de control. El juego los muestra como texto plano.

## 5. Servidor

- **`server.json` nuevo:** `Relay` (bool, `true`), `RelayAddress` (texto, por defecto el de `ProtocolInfo`) y `Public` (bool, `false`).
- **`NetworkServer`:**
  - `Adopt(TcpClient, string remoteLabel)` para las conexiones del relay.
  - Si la primera trama de una conexión es `RelayProbe`, `ServerStatusRequest` o `ServerControl`, la contesta y cierra, sin crear jugador.
  - En UDP, contesta a `RelayProbe` con ficha 0.
- **`RelayLink` (nuevo):** se encarga de la conexión de control, los latidos, el "hola" UDP, las reconexiones, enganchar sesiones y avisar de las fichas. Lleva el estado: `Desactivado`, `Conectando`, `Registrado(código, directo sí/no)` o `Rechazado(motivo)`.
- **Consulta de estado (en el puerto de juego):** `ServerStatusRequest` → `ServerStatusResponse { nombre, jugadores, máximo, contraseña, estado del relay, código, directo, público, versiones }`. La puede hacer cualquiera.
- **Control local:** `ServerControl { volver a comprobar | público sí/no }`. **Solo se acepta desde loopback**, comprobado con la dirección real del socket y nunca con la etiqueta de `Adopt`: una conexión que llega por el relay sale del relay, no de loopback. El cambio de `Public` se guarda en `server.json`.
- La consulta de estado revela el código a quien ya conoce la IP del servidor. Se acepta, porque con la IP ya se puede entrar.
- **Consola:** `relay` (código, estado y acceso) y `public on|off`.
- **Dentro de la partida:** al entrar, el servidor manda a cada jugador `ServerCodeMessage { código, directo }`, y otra vez si cambia.

## 6. Mod (cliente)

- **Ventana de conexión:**
  - El campo se llama "Servidor (IP o código)". Si es un código, la consulta al relay se hace en un hilo aparte y luego se sigue el orden de 4.3.
  - Se mantiene `dirección:puerto` pegado de una vez.
  - Botón nuevo: **Lista**.
- **Ventana de servidores públicos:**
  - Tabla con nombre, jugadores, 🔒, versión y conexión (directa o relay), ordenada por jugadores.
  - Casilla "ocultar los que tienen contraseña", buscador por nombre y botón Actualizar.
  - **Unirse:** si no hay contraseña, entra. Si la hay, rellena el código y pide escribirla.
  - Los servidores llenos o con otro protocolo salen en gris y con el motivo.
- **Ventana Hospedar:**
  - Mientras está abierta, consulta el estado a `127.0.0.1:<puerto>` cada 3 s.
  - Muestra si el servidor está funcionando, el **código [Copiar]**, "✓ Accesible directamente" o "Por el relay", el botón **Volver a comprobar** y la casilla **Mostrar en la lista pública**.
  - Las pestañas de la 0.1.1 pasan a un apartado plegado "Avanzado".
  - Si no encuentra el servidor: "No encuentro tu servidor en este PC: ábrelo primero". Si el servidor es 0.1.x: "Tu servidor es de una versión anterior: actualízalo".
- **Ventana de MultiKerbal en la partida:** fila "Código: K7M-Q2P [Copiar]", visible para todos.
- **`settings.cfg`:** `relayAddress`, por si alguien usa otro relay. Por defecto, el de `ProtocolInfo`.

## 7. Relay

- **Programa de consola** en `net10.0`. Se publica para `linux-arm64` (Oracle Ampere) y para `win-x64` (pruebas). Guarda sus datos en `relay-data/`.
- **Componentes:**
  - `ServerRegistry`: claves, códigos, baneos y servidores vivos.
  - `SessionBroker`: emparejar jugador y servidor y pasar los bytes.
  - `UdpForwarder`: reenvío por ficha.
  - `ReachabilityProber`: las comprobaciones de acceso directo.
  - `Limits`: los límites contra abusos.
  - Consola.
- **Límites:**
  - Como máximo **5 servidores por IP** y **64 sesiones por servidor**.
  - **1 MB/s por sesión** en cada sentido, por TCP y por UDP.
  - Las conexiones que no mandan un primer mensaje válido en **10 s** se cierran.
  - Máximo de 64 KB por mensaje del relay.
- **Consola:**
  - `list`: servidores vivos, sesiones y tráfico.
  - `ban <código>`: lo saca de la lista, desconecta y bloquea la clave.
  - `unban <código>`.
  - `stop`.
- **Registro:** altas y bajas de servidores, sesiones, rechazos y tráfico por hora. **No guarda el contenido** de lo que pasa.

## 8. Fallos

| Situación | Lo que pasa |
|---|---|
| Relay caído | El servidor funciona por IP y reintenta (4.1). El mod: "No se puede contactar con el relay: los códigos y la lista no funcionan ahora; puedes conectar por IP". |
| Código desconocido o servidor apagado | "No hay ningún servidor encendido con ese código". |
| El servidor no se engancha | "El servidor no responde a través del relay". |
| Reinicio del relay | Los servidores se vuelven a registrar solos y conservan el código. Los jugadores que iban por el relay se desconectan y vuelven a entrar. |
| Protocolo distinto (en la consulta) | "Ese servidor usa otra versión de MultiKerbal (x.y.z)", antes de intentar entrar. |

## 9. Compatibilidad y versión

- El protocolo del juego pasa de **5 a 6**, por los mensajes nuevos que recibe el mod. Ya estaba en 5 por los cambios de progreso compartido y cuentas, posteriores a la 0.1.1.
- **Los mods 0.1.x no pueden entrar a servidores 0.2.0, ni al revés.** Reciben el aviso de versión incompatible que ya existe.
- La versión de todo pasa a **0.2.0**, publicada primero como beta.
- Los datos del juego por el relay **van sin cifrar**, como por IP. Se dirá en el README y en `docs/RELAY.md`.

## 10. Pruebas

- **Unitarias:**
  - Los mensajes del relay, de ida y vuelta.
  - Normalización y validación de códigos.
  - Límites (velocidad, servidores por IP).
  - El enrutado UDP por ficha.
- **Integración, en un solo proceso de test** (relay, servidor y `TestClient` en loopback, con puertos libres):
  - Registro y código permanente tras reiniciar el relay.
  - Entrar con código cuando el servidor es directo y cuando va por el relay.
  - Que el directo caiga al relay si falla.
  - UDP a través del relay (vínculo y estados de nave).
  - Lista pública (público y privado, ordenada).
  - Consulta de estado y control solo desde loopback.
  - Comprobación de acceso, simulando "no accesible" con un puerto anunciado distinto del real.
  - Ban.
  - Reconexión del servidor tras caerse el relay.
- **Real:**
  1. Relay en Oracle, el servidor del autor en casa (irá por el relay) y un segundo KSP en `E:\KSP-Dev2`.
  2. Después, un amigo desde fuera.

## 11. Fases

1. **Relay base:** identidad y códigos, registro, latidos, entrar por relay por TCP y UDP, `Adopt` en el servidor y conectar con código en `NetClient`.
2. **Acceso directo:** las comprobaciones, la dirección directa en la consulta y el plazo de 3 s con caída al relay.
3. **Lista pública:** latidos con datos, `ListRequest` y `public on|off`.
4. **Juego:**
   - La consulta de estado y el control local.
   - Las ventanas: conexión, lista, Hospedar y el código dentro de la partida.
   - El protocolo 6.
5. **Puesta en marcha:**
   - El autor crea la cuenta de Oracle Cloud y DuckDNS.
   - Instalación por SSH como servicio `systemd`, con los puertos abiertos.
   - `docs/RELAY.md`, README y guía de hospedar actualizados.
   - Release 0.2.0 beta.
