# MultiKerbal

Mod multijugador para **Kerbal Space Program 1.12.5**: servidor dedicado y un único reloj compartido por todos.

*[Read me in English](README.md) · la interfaz del juego está en español e inglés.*

> Estado: pronto pero jugable. Ya están la conexión, el chat, el reloj compartido con ajustes de warp, las naves visibles entre jugadores, el dueño y el acceso de cada nave, el control de mods, el acoplamiento entre jugadores y el control compartido. El diseño y la hoja de ruta, en [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md).

## Qué hace

- **Un solo reloj.** El universo avanza al warp **más lento** de los jugadores que están en el Centro Espacial, la estación de seguimiento o en vuelo. Nadie viaja al pasado, así que el guardado rápido, la carga rápida y revertir están desactivados.
- **Ajustes de warp.** Aceptar automáticamente el warp de los demás hasta un máximo y solo con las condiciones que marques, aceptarlo mientras estás ausente, o rechazarlo automáticamente.
- **Naves entre jugadores.** Todos ven las naves de todos, en vuelo, en el mapa y en la estación de seguimiento, con una etiqueta con su dueño. Las etiquetas se filtran por tipo de nave, como los filtros del mapa.
- **Dueño y acceso por nave.** Cada nave es de quien la lanzó (se guarda en disco) y es *privada*, *compartida* o *pública*: eso decide quién más puede pilotarla, destruirla, recuperarla o borrarla. El dueño puede prestarla, cederla, regalarla o dejarla sin dueño.
- **Préstamos.** El dueño presta una nave (o un kerbal en EVA) a un jugador, que puede pilotarla aunque sea privada, y la recupera cuando quiere. Si quien la tiene prestada la destruye, vuelve a donde estaba cuando la tomó.
- **Un kerbal, un jugador.** Un kerbal no puede ir a la vez en naves de dos jugadores.
- **Cuentas.** Cada jugador entra con su nombre y su propia contraseña: nadie más puede usar tu nombre ni tus naves.
- **Progreso compartido (en pruebas).** En partidas de ciencia o carrera, la ciencia, las tecnologías, los experimentos hechos, los fondos, la reputación, las piezas compradas, los edificios y los contratos aceptados son de todos. El plantel (contrataciones y muertes) se comparte siempre.
- **Acoplamiento entre jugadores.** Cuando una nave libre se acerca a menos de 2,2 km, tu partida se encarga de su física: los puertos se tocan, las naves chocan y se funden como en un jugador.
- **Control compartido.** Quien pilota puede nombrar copiloto a otro jugador: los mandos del copiloto viajan por la red y se suman a los suyos. También puedes pedir el control de una nave ajena.
- **Control de mods.** El servidor recuerda la lista de mods del primer jugador que entra. Antes de entrar en la partida ves esa lista comparada con la tuya: en verde lo que coincide y en rojo lo que falta o sobra.

## Instalación

Hacen falta dos cosas: **el mod**, que instala cada jugador en su KSP, y **el servidor**, que solo lo abre una persona. Son descargas separadas en la [página de releases](https://github.com/alonso49noob/MultiKerbal/releases):

| Release | Archivo | Para |
|---|---|---|
| **MultiKerbal** *x.y.z* | `MultiKerbal-x.y.z-mod.zip` | Cada jugador (lado del cliente). |
| **MultiKerbal Server** *x.y.z* | `MultiKerbal-Server-x.y.z.zip` | Quien aloja la partida (lado del servidor). |

Todos, servidor incluido, deben usar el mismo número de versión.

### 1. El mod (cada jugador)

**A mano:**

1. Descarga `MultiKerbal-<versión>-mod.zip` de la [página de releases](https://github.com/alonso49noob/MultiKerbal/releases) o de [SpaceDock](https://spacedock.info/mod/4611/MultiKerbal) (es el mismo archivo).
2. Descomprímelo. Sale una sola carpeta, `MultiKerbal`.
3. Mueve esa carpeta a la carpeta `GameData` de tu KSP (junto a la carpeta `Squad`):

   ```
   Kerbal Space Program/
   └── GameData/
       ├── Squad/
       └── MultiKerbal/          ← la carpeta del zip
           ├── LICENSE
           ├── MultiKerbal.version
           ├── README.md
           └── Plugins/
               ├── MultiKerbal.Client.dll
               └── MultiKerbal.Common.dll
   ```

4. Abre KSP. En el menú principal aparece la ventana **MultiKerbal**.

**Con CKAN:** el mod está [enviado](https://github.com/KSP-CKAN/NetKAN/pull/11575) y esperando revisión. Cuando aparezca, busca *MultiKerbal* en CKAN e instálalo como cualquier otro mod.

El mod no necesita otros mods. Solo funciona con KSP 1.12.x.

### 2. El servidor (una persona)

1. Instala el **[.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** (basta con el "Runtime", no hace falta el SDK).
2. Descarga `MultiKerbal-Server-<versión>.zip` de la release **MultiKerbal Server** y descomprímelo donde quieras, **no** dentro de KSP. Sale una carpeta, `MultiKerbal-Server`.
3. Arráncalo desde esa carpeta:
   - **Windows:** doble clic en `MultiKerbal.Server.exe`. Si el Firewall de Windows pregunta, permítelo.
   - **Linux / macOS:** ejecuta `dotnet MultiKerbal.Server.dll`.
4. La primera vez crea `server.json` junto al servidor. Ciérralo (escribe `stop`), pon en ese archivo un nombre y una contraseña si quieres, y vuelve a abrirlo. Ver [Ajustes del servidor](#ajustes-del-servidor).
5. Pasa a los demás la dirección que deben usar:
   - **Mismo PC:** `127.0.0.1`
   - **Misma red de casa:** tu IP local (en Windows, `ipconfig` → *Dirección IPv4*, p. ej. `192.168.1.20`).
   - **Por Internet:** hay cuatro formas: red local virtual (Tailscale), túnel (playit.gg), abrir el puerto **6750** (TCP y UDP) en el router o un servidor en la nube. El botón **Hospedar** de la ventana MultiKerbal te ayuda con cualquiera de ellas desde el juego, y [docs/HOSTING.es.md](docs/HOSTING.es.md) las explica paso a paso.

El universo se guarda en la carpeta `Universe`, junto al servidor, cada minuto y siempre que lo cierras con `stop` o Ctrl+C. Haz copia de esa carpeta si no quieres perderlo.

### 3. Conectarse

En el menú principal de KSP, rellena la ventana MultiKerbal: tu nombre, la dirección del servidor (si pegas `dirección:puerto` de una vez, el puerto se rellena solo), el puerto (`6750` si no lo cambiaste) y la clave del servidor si la tiene. Pulsa **Conectar y jugar**.

**Tu contraseña** es la de tu cuenta de jugador, distinta de la clave del servidor. La primera vez que entras con un nombre, ese nombre queda registrado con la contraseña que pongas (al menos 4 caracteres); después solo entra quien la sepa. Si la olvidas, el administrador puede borrar tu cuenta (`account delete <nombre>`) y eliges otra al volver a entrar. *Recordarla* la guarda en este ordenador.

Si tus mods no coinciden con los del servidor, antes de empezar ves una lista con lo que falta y decides si entrar igualmente.

### Actualizar y desinstalar

- **Actualizar:** borra la carpeta `GameData/MultiKerbal` vieja y mete la nueva. Actualiza el servidor a la vez: un servidor y un mod de versiones distintas pueden no entenderse. Para actualizar el servidor, cambia sus archivos pero conserva `server.json` y la carpeta `Universe`.
- **Desinstalar:** borra `GameData/MultiKerbal`. MultiKerbal guarda su copia local de la partida en `saves/MultiKerbal` y sus ajustes en `PluginData/MultiKerbal`; puedes borrar las dos. Tus otras partidas no se tocan nunca.

## Jugar

1. Arranca el servidor.
2. Conéctate desde el menú principal de KSP.
3. Se crea una partida local (sandbox, ciencia o carrera, según el servidor) sincronizada con él. El botón de MultiKerbal en la barra de aplicaciones abre jugadores, naves y chat.

**Warp** (botón *Warp* en la ventana de MultiKerbal):

- *Aceptar automáticamente* el warp que pidan los demás, hasta un máximo, solo si se cumple todo lo que marques: no estar pilotando, nave en órbita o posada, motores apagados, sin otras naves a menos de 2,5 km.
- *Aceptar si estoy ausente*: tras el tiempo que elijas sin tocar teclado ni ratón (30 s a 10 min), se acepta hasta el mismo máximo sin mirar las demás condiciones.
- *Rechazar automáticamente* si se cumple cualquiera de lo que marques. Sin nada marcado, rechaza siempre (también en el hangar). Mientras esté activado no se acepta nada automáticamente.
- El warp físico (x2 a x4) nunca se acepta automáticamente.

**Naves:** el botón *Nave* junto a cada nave de la lista muestra su dueño y su acceso, y deja prestarla, regalarla, cederla, pedirla o elegir copiloto. *Tus naves nuevas* decide con qué acceso nacen tus lanzamientos.

- *Prestarla* a un jugador: puede pilotarla aunque sea privada, pero no destruirla, recuperarla ni borrarla. Si la estás pilotando, además se la pasas. *Recuperarla* se la quita (si la está pilotando, deja de hacerlo); quien la tiene prestada puede *devolverla*.
- Destruir, recuperar o borrar una nave ajena no se puede sin permiso de su dueño (que sea pública), aunque la estés pilotando. Si la estrellas, vuelve a como la dejó su dueño. Acoplarla a otra sí se puede.
- Las etapas que sueltes de una nave ajena siguen siendo de su dueño (pero las puedes perder: se queman, se estrellan).
- Si te devuelven una nave dañada, *Devolverla a como la dejaste* la deja como estaba antes de prestarla, mientras nadie la pilote. Si vuelves a pilotarla tú, se queda como está.
- Un kerbal que va en la nave de otro jugador no sale en tu hangar. Si lanzas una nave con un kerbal que ya está en la nave de otro, el servidor no la acepta y vuelves al Centro Espacial.

**Mirar:** el botón *Mirar* junto a una nave que no pilotas lleva la cámara hasta ella, esté donde esté, con los mandos bloqueados. Si está lejos, se carga el vuelo a su alrededor; si estabas pilotando, tu nave tiene que estar estable: posada y quieta, o en órbita con los motores apagados. *Dejar de mirar* te devuelve a tu nave o a la escena de la que venías.

**Progreso compartido (en pruebas):** con `GameMode` en `science` o `career`, la ciencia, las tecnologías y los experimentos ya hechos son de todos. En carrera también los fondos, la reputación, las piezas compradas, las mejoras de edificios y los contratos: las ofertas son de cada uno, pero un contrato aceptado lo tienen todos y lo cobra el primero que lo cumple. En cualquier modo, un kerbal contratado o muerto lo está para todos.

## Ajustes del servidor

`server.json`:

| Ajuste | Por defecto | Qué hace |
|---|---|---|
| `ServerName` | `Servidor MultiKerbal` | Nombre que ven los jugadores. |
| `Port` | `6750` | Puerto TCP y UDP. |
| `MaxPlayers` | `8` | Jugadores a la vez. |
| `Password` | *(vacío)* | Clave del servidor, la misma para todos. Vacío es sin clave. Aparte, cada jugador tiene su propia contraseña de cuenta. |
| `Motd` | texto de bienvenida | Mensaje que sale en el chat al entrar. |
| `PauseClockWhenEmpty` | `true` | Para el reloj del universo mientras no hay nadie. |
| `ModPolicy` | `warn` | `off` no mira los mods, `warn` deja entrar a todos y avisa de las diferencias, `strict` rechaza a quien no los tenga iguales. |
| `Language` | `es` | Idioma de los mensajes de chat y motivos de rechazo que manda el servidor: `es` o `en`. |
| `GameMode` | `sandbox` | `sandbox` (sin progreso), `science` (ciencia y tecnologías compartidas) o `career` (además fondos y reputación). |
| `StartingFunds` | `25000` | Fondos con los que empieza una carrera nueva. |
| `DataDirectory` | `Universe` | Dónde se guarda el universo. |

Comandos de consola: `list`, `vessels`, `owner <nave> <jugador|nadie> [privada|compartida|publica]`, `account` (cuentas) y `account delete <jugador>` (si alguien olvidó su contraseña), `progress`, `mods [jugador|set <jugador>]`, `warp`, `say <texto>`, `kick <jugador> [motivo]`, `time`, `save`, `stop`. Escribe `help` para verlos en la consola.

## Compilar desde el código

| Proyecto | Destino | Qué es |
|---|---|---|
| `src/MultiKerbal.Common` | `net472` + `net10.0` | Protocolo, serialización, transporte TCP/UDP y lógica de tiempo. Sin dependencias. |
| `src/MultiKerbal.Server` | `net10.0` | Servidor dedicado de consola. Autoridad del universo. |
| `src/MultiKerbal.Client` | `net472` | Plugin de KSP (se copia a `GameData/MultiKerbal/Plugins`). |
| `tests/MultiKerbal.Tests` | `net10.0` | Tests unitarios y de integración cliente-servidor reales. |

El mod no lleva dependencias: el Mono de KSP no trae `netstandard.dll`, así que el transporte está escrito desde cero.

Hace falta el .NET SDK 10 y KSP 1.12.5. Para desarrollar se recomienda una copia limpia (solo Squad/DLC), p. ej. `E:\KSP-Dev`.

1. Copia `LocalDev.props.example` como `LocalDev.props` y ajusta `KSPDir`.
2. Compila y ejecuta los tests:

```bash
dotnet build MultiKerbal.sln
```

```bash
dotnet test MultiKerbal.sln
```

Con `DeployToKSP=true`, cada compilación del cliente copia el mod a `$(KSPDir)\GameData\MultiKerbal`. Para arrancar el servidor desde el código:

```bash
dotnet run --project src/MultiKerbal.Server -- --port 6750
```

Para sacar una versión, sube `<Version>` en `Directory.Build.props`, haz commit y ejecuta el script de abajo. Compila desde el último commit (lo que no esté guardado se queda fuera) y deja los dos zips en `dist/`: el del mod para la release **MultiKerbal** y el del servidor para la release **MultiKerbal Server**.

```bash
powershell -ExecutionPolicy Bypass -File tools/Package.ps1
```

### Dos jugadores en un solo PC

`E:\KSP-Dev2` es una segunda instancia enlazada (uniones de directorio y enlaces duros) a `E:\KSP-Dev`: comparte juego y mods pero tiene su propio `settings.cfg`, partidas, logs y nombre de jugador (`PluginData/MultiKerbal/settings.cfg`).

**Ábrelas de una en una:** arranca la segunda cuando la primera ya esté en el menú principal. Mientras carga, KSP abre en exclusiva los archivos de `GameData`; si las dos cargan a la vez, fallan los modelos de las piezas (`IOException: Sharing violation` en `KSP.log`) y una de ellas puede quedarse colgada.

## Licencia

MIT, en [LICENSE](LICENSE). Los avisos de fallos y los pull requests son bienvenidos.
