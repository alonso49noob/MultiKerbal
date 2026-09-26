# MultiKerbal

Mod multijugador para **Kerbal Space Program 1.12.5**: servidor dedicado y un único reloj compartido por todos.

*[Read me in English](README.md) · la interfaz del juego está en español e inglés.*

> Estado: pronto pero jugable. Ya están la conexión, el chat, el reloj compartido con ajustes de warp, las naves visibles entre jugadores, el dueño y el acceso de cada nave, el control de mods, el acoplamiento entre jugadores y el control compartido. El diseño y la hoja de ruta, en [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md).

## Qué hace

- **Un solo reloj.** El universo avanza al warp **más lento** de los jugadores que están en el Centro Espacial, la estación de seguimiento o en vuelo. Nadie viaja al pasado, así que el guardado rápido, la carga rápida y revertir están desactivados.
- **Ajustes de warp.** Aceptar automáticamente el warp de los demás hasta un máximo y solo con las condiciones que marques, aceptarlo mientras estás ausente, o rechazarlo automáticamente.
- **Naves entre jugadores.** Todos ven las naves de todos, en vuelo, en el mapa y en la estación de seguimiento, con una etiqueta con su dueño. Las etiquetas se filtran por tipo de nave, como los filtros del mapa.
- **Dueño y acceso por nave.** Cada nave es de quien la lanzó (se guarda en disco) y es *privada*, *compartida* o *pública*: eso decide quién más puede pilotarla, recuperarla o borrarla. El dueño puede cederla, regalarla o dejarla sin dueño.
- **Acoplamiento entre jugadores.** Cuando una nave libre se acerca a menos de 2,2 km, tu partida se encarga de su física: los puertos se tocan, las naves chocan y se funden como en un jugador.
- **Control compartido.** Quien pilota puede nombrar copiloto a otro jugador: los mandos del copiloto viajan por la red y se suman a los suyos. También puedes pedir el control o mirar una nave cercana con los mandos bloqueados.
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

1. Descarga `MultiKerbal-<versión>-mod.zip`.
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
   - **Por Internet:** tu IP pública, y en el router redirige el puerto **6750**, tanto **TCP como UDP**, al PC del servidor.

El universo se guarda en la carpeta `Universe`, junto al servidor, cada minuto y siempre que lo cierras con `stop` o Ctrl+C. Haz copia de esa carpeta si no quieres perderlo.

### 3. Conectarse

En el menú principal de KSP, rellena la ventana MultiKerbal: tu nombre, la dirección del servidor, el puerto (`6750` si no lo cambiaste) y la contraseña si la hay. Pulsa **Conectar y jugar**.

Si tus mods no coinciden con los del servidor, antes de empezar ves una lista con lo que falta y decides si entrar igualmente.

### Actualizar y desinstalar

- **Actualizar:** borra la carpeta `GameData/MultiKerbal` vieja y mete la nueva. Actualiza el servidor a la vez: un servidor y un mod de versiones distintas pueden no entenderse. Para actualizar el servidor, cambia sus archivos pero conserva `server.json` y la carpeta `Universe`.
- **Desinstalar:** borra `GameData/MultiKerbal`. MultiKerbal guarda su copia local de la partida en `saves/MultiKerbal` y sus ajustes en `PluginData/MultiKerbal`; puedes borrar las dos. Tus otras partidas no se tocan nunca.

## Jugar

1. Arranca el servidor.
2. Conéctate desde el menú principal de KSP.
3. Se crea una partida sandbox local sincronizada con el servidor. El botón de MultiKerbal en la barra de aplicaciones abre jugadores, naves y chat.

**Warp** (botón *Warp* en la ventana de MultiKerbal):

- *Aceptar automáticamente* el warp que pidan los demás, hasta un máximo, solo si se cumple todo lo que marques: no estar pilotando, nave en órbita o posada, motores apagados, sin otras naves a menos de 2,5 km.
- *Aceptar si estoy ausente*: tras el tiempo que elijas sin tocar teclado ni ratón (30 s a 10 min), se acepta hasta el mismo máximo sin mirar las demás condiciones.
- *Rechazar automáticamente* si se cumple cualquiera de lo que marques. Sin nada marcado, rechaza siempre (también en el hangar). Mientras esté activado no se acepta nada automáticamente.
- El warp físico (x2 a x4) nunca se acepta automáticamente.

**Naves:** el botón *Nave* junto a cada nave de la lista muestra su dueño y su acceso, y deja regalarla, cederla, pedirla, mirarla o elegir copiloto. *Tus naves nuevas* decide con qué acceso nacen tus lanzamientos.

El jugador se identifica solo por su nombre: mientras el dueño de una nave no esté conectado, otro podría entrar con ese nombre. Por ahora la única protección es la contraseña del servidor.

## Ajustes del servidor

`server.json`:

| Ajuste | Por defecto | Qué hace |
|---|---|---|
| `ServerName` | `Servidor MultiKerbal` | Nombre que ven los jugadores. |
| `Port` | `6750` | Puerto TCP y UDP. |
| `MaxPlayers` | `8` | Jugadores a la vez. |
| `Password` | *(vacío)* | Vacío es sin contraseña. |
| `Motd` | texto de bienvenida | Mensaje que sale en el chat al entrar. |
| `PauseClockWhenEmpty` | `true` | Para el reloj del universo mientras no hay nadie. |
| `ModPolicy` | `warn` | `off` no mira los mods, `warn` deja entrar a todos y avisa de las diferencias, `strict` rechaza a quien no los tenga iguales. |
| `Language` | `es` | Idioma de los mensajes de chat y motivos de rechazo que manda el servidor: `es` o `en`. |
| `DataDirectory` | `Universe` | Dónde se guarda el universo. |

Comandos de consola: `list`, `vessels`, `owner <nave> <jugador|nadie> [privada|compartida|publica]`, `mods [jugador|set <jugador>]`, `warp`, `say <texto>`, `kick <jugador> [motivo]`, `time`, `save`, `stop`. Escribe `help` para verlos en la consola.

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
