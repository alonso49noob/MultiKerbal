# MultiKerbal

Mod multijugador para **Kerbal Space Program 1.12.5** con servidor dedicado y un único reloj compartido.

> Estado: fases 1 y 2 probadas con dos jugadores: conexión, chat, reloj compartido con ajustes de warp (aceptar, rechazar y aceptar por ausencia) y naves visibles entre jugadores en el mapa y en la estación de seguimiento. Fase 3 (interacción entre naves) pendiente. Ver [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md) para el diseño y la hoja de ruta.

## Estructura

| Proyecto | Destino | Qué es |
|---|---|---|
| `src/MultiKerbal.Common` | `net472` + `net10.0` | Protocolo, serialización, transporte TCP/UDP y lógica de tiempo. Sin dependencias. |
| `src/MultiKerbal.Server` | `net10.0` | Servidor dedicado de consola. Autoridad del universo. |
| `src/MultiKerbal.Client` | `net472` | Plugin de KSP (se copia a `GameData/MultiKerbal/Plugins`). |
| `tests/MultiKerbal.Tests` | `net10.0` | Tests unitarios y de integración cliente-servidor reales. |

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

La primera vez crea `server.json` (nombre, puerto, contraseña, máximo de jugadores, MOTD) en el directorio de trabajo y guarda el universo en `Universe/`. Abre el puerto **TCP y UDP** si juegas por Internet.

Comandos de consola: `list`, `vessels`, `owner <nave> <jugador|nadie> [privada|compartida|publica]`, `warp`, `say <texto>`, `kick <jugador> [motivo]`, `time`, `save`, `stop`.

## Jugar

1. Arranca el servidor.
2. En el menú principal de KSP aparece la ventana **MultiKerbal**: nombre, servidor, puerto y *Conectar y jugar*.
3. Se crea una partida sandbox local sincronizada con el servidor. El botón de MultiKerbal en la barra de aplicaciones abre jugadores y chat.

Reglas del reloj compartido: el universo avanza al warp **más lento** de los jugadores que están en el Centro Espacial, la estación de seguimiento o en vuelo (en el hangar no se bloquea a nadie). Guardado rápido, carga rápida y revertir vuelo están desactivados porque viajarían al pasado.

**Ajustes de warp** (botón en la ventana de MultiKerbal):

- *Aceptar automáticamente* el warp que pidan los demás, hasta un máximo, solo si se cumple todo lo que marques: no estar pilotando, nave en órbita o posada, motores apagados, sin otras naves a menos de 2,5 km.
- *Aceptar si estoy ausente*: tras el tiempo que elijas sin tocar teclado ni ratón (30 s a 10 min), se acepta hasta el mismo máximo sin mirar las demás condiciones. Al volver a mover el ratón deja de aceptar.
- *Rechazar automáticamente* si se cumple cualquiera de lo que marques: estar pilotando, estar en el Centro Espacial, nave en la atmósfera, otras naves cerca. Sin nada marcado, rechaza siempre (también en el hangar). Mientras esté activado no se acepta nada automáticamente, aunque ninguna de sus condiciones se cumpla: en ese caso decides tú.
- Si no se aplica ninguna de las dos, tienes que subir el warp tú para acompañar a los demás. Una petición que nadie acompaña se cancela a los 15 s (o con la tecla de bajar warp).
- El warp físico (x2 a x4) nunca se acepta automáticamente.

## Probar con dos jugadores en un solo PC

`E:\KSP-Dev2` es una segunda instancia enlazada (uniones de directorio y enlaces duros) a `E:\KSP-Dev`: comparte juego y mods pero tiene su propio `settings.cfg`, partidas, logs y nombre de jugador (`PluginData/MultiKerbal/settings.cfg`).

**Ábrelas de una en una:** arranca la segunda cuando la primera ya esté en el menú principal. Mientras carga, KSP abre en exclusiva los archivos de `GameData`; si las dos cargan a la vez, fallan los modelos de las piezas (`IOException: Sharing violation` en `KSP.log`) y una de ellas puede quedarse colgada.
