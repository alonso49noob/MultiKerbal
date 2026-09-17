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

## Estructura

| Proyecto | Destino | Qué es |
|---|---|---|
| `src/MultiKerbal.Common` | `net472` + `net10.0` | Protocolo, serialización, transporte TCP/UDP y lógica de tiempo. Sin dependencias. |
| `src/MultiKerbal.Server` | `net10.0` | Servidor dedicado de consola. Autoridad del universo. |
| `src/MultiKerbal.Client` | `net472` | Plugin de KSP (se copia a `GameData/MultiKerbal/Plugins`). |
| `tests/MultiKerbal.Tests` | `net10.0` | Tests unitarios y de integración cliente-servidor reales. |

El mod no lleva dependencias: el Mono de KSP no trae `netstandard.dll`, así que el transporte está escrito desde cero.

## Requisitos

- .NET SDK 10
- KSP 1.12.5. Para desarrollar se recomienda una copia limpia (solo Squad/DLC), p. ej. `E:\KSP-Dev`.

## Compilar

1. Copia `LocalDev.props.example` como `LocalDev.props` y ajusta `KSPDir`.
2. Compila y ejecuta los tests:

```bash
dotnet build MultiKerbal.sln
```

```bash
dotnet test MultiKerbal.sln
```

Con `DeployToKSP=true`, cada compilación del cliente copia el mod a `$(KSPDir)\GameData\MultiKerbal`.

## Servidor

```bash
dotnet run --project src/MultiKerbal.Server -- --port 6750
```

La primera vez crea `server.json` (nombre, puerto, contraseña, máximo de jugadores, MOTD, control de mods e idioma) en el directorio de trabajo y guarda el universo en `Universe/`. Abre el puerto **TCP y UDP** si juegas por Internet.

Comandos de consola: `list`, `vessels`, `owner <nave> <jugador|nadie> [privada|compartida|publica]`, `mods [jugador|set <jugador>]`, `warp`, `say <texto>`, `kick <jugador> [motivo]`, `time`, `save`, `stop`.

`ModPolicy` en `server.json`: `off` (no se miran los mods), `warn` (por defecto: entra todo el mundo y se avisa de las diferencias) o `strict` (se rechaza a quien no los tenga iguales).

## Jugar

1. Arranca el servidor.
2. En el menú principal de KSP aparece la ventana **MultiKerbal**: nombre, servidor, puerto, idioma y *Conectar y jugar*.
3. Se crea una partida sandbox local sincronizada con el servidor. El botón de MultiKerbal en la barra de aplicaciones abre jugadores, naves y chat.

**Ajustes de warp** (botón *Warp* en la ventana de MultiKerbal):

- *Aceptar automáticamente* el warp que pidan los demás, hasta un máximo, solo si se cumple todo lo que marques: no estar pilotando, nave en órbita o posada, motores apagados, sin otras naves a menos de 2,5 km.
- *Aceptar si estoy ausente*: tras el tiempo que elijas sin tocar teclado ni ratón (30 s a 10 min), se acepta hasta el mismo máximo sin mirar las demás condiciones.
- *Rechazar automáticamente* si se cumple cualquiera de lo que marques. Sin nada marcado, rechaza siempre (también en el hangar). Mientras esté activado no se acepta nada automáticamente.
- El warp físico (x2 a x4) nunca se acepta automáticamente.

El jugador se identifica solo por su nombre: mientras el dueño de una nave no esté conectado, otro podría entrar con ese nombre. Por ahora la única protección es la contraseña del servidor.

## Probar con dos jugadores en un solo PC

`E:\KSP-Dev2` es una segunda instancia enlazada (uniones de directorio y enlaces duros) a `E:\KSP-Dev`: comparte juego y mods pero tiene su propio `settings.cfg`, partidas, logs y nombre de jugador (`PluginData/MultiKerbal/settings.cfg`).

**Ábrelas de una en una:** arranca la segunda cuando la primera ya esté en el menú principal. Mientras carga, KSP abre en exclusiva los archivos de `GameData`; si las dos cargan a la vez, fallan los modelos de las piezas (`IOException: Sharing violation` en `KSP.log`) y una de ellas puede quedarse colgada.
