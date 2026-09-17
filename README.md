# MultiKerbal

Multiplayer mod for **Kerbal Space Program 1.12.5**: a dedicated server and one clock shared by everyone.

*[Léeme en español](README.es.md) · the in-game UI speaks Spanish and English.*

> Status: early but playable. Connection, chat, the shared clock with warp settings, vessels visible between players, per-vessel ownership, mod checking, docking between players and shared control are all in. See [docs/ARQUITECTURA.md](docs/ARQUITECTURA.md) (in Spanish) for the design and the roadmap.

## What it does

- **One shared clock.** The universe runs at the *slowest* warp among the players who are at the Space Center, the tracking station or in flight. Nobody is ever pushed into the past, so quicksave, quickload and revert are disabled.
- **Warp settings.** Accept other players' warp automatically up to a maximum and only under the conditions you tick, accept it while you are away from the keyboard, or refuse it automatically.
- **Vessels between players.** Everyone sees everyone's vessels, in flight, in the map and in the tracking station, with a label showing the owner. Labels can be filtered by vessel type, like the map filters.
- **Owner and access per vessel.** Each vessel belongs to the player who launched it (kept on disk) and is *private*, *shared* or *public*: that decides who else can fly it, recover it or delete it. Owners can hand a vessel over, give it away or leave it without an owner.
- **Docking between players.** When a free vessel comes within 2.2 km, your game takes over its physics, so ports touch, things collide and vessels merge like in single player.
- **Shared control.** Whoever flies a vessel can name another player co-pilot: the co-pilot's controls travel over the network and add to the pilot's. You can also ask for control, or just watch a nearby vessel with the controls locked.
- **Mod checking.** The server remembers the mod list of the first player who joins. Before you enter the game you get the list compared with yours, green for what matches and red for what is missing or extra.

## Layout

| Project | Target | What it is |
|---|---|---|
| `src/MultiKerbal.Common` | `net472` + `net10.0` | Protocol, serialization, TCP/UDP transport and time logic. No dependencies. |
| `src/MultiKerbal.Server` | `net10.0` | Dedicated console server. Authority over the universe. |
| `src/MultiKerbal.Client` | `net472` | KSP plugin (copied to `GameData/MultiKerbal/Plugins`). |
| `tests/MultiKerbal.Tests` | `net10.0` | Unit tests plus real client-server integration tests. |

The mod ships no dependencies: KSP's Mono has no `netstandard.dll`, so the transport is written from scratch.

## Requirements

- .NET SDK 10
- KSP 1.12.5. For development a clean copy (Squad/DLC only) is recommended, e.g. `E:\KSP-Dev`.

## Build

1. Copy `LocalDev.props.example` to `LocalDev.props` and point `KSPDir` at your KSP folder.
2. Build and run the tests:

```bash
dotnet build MultiKerbal.sln
```

```bash
dotnet test MultiKerbal.sln
```

With `DeployToKSP=true`, every client build copies the mod into `$(KSPDir)\GameData\MultiKerbal`.

## Server

```bash
dotnet run --project src/MultiKerbal.Server -- --port 6750
```

The first run creates `server.json` (name, port, password, max players, MOTD, mod policy, language) in the working directory and keeps the universe in `Universe/`. Open the port for **TCP and UDP** if you play over the Internet.

Console commands: `list`, `vessels`, `owner <vessel> <player|nobody> [private|shared|public]`, `mods [player|set <player>]`, `warp`, `say <text>`, `kick <player> [reason]`, `time`, `save`, `stop`.

`ModPolicy` in `server.json`: `off` (ignore mods), `warn` (default: everyone gets in, differences are announced) or `strict` (refuse players whose mods differ).

## Playing

1. Start the server.
2. KSP's main menu shows the **MultiKerbal** window: name, server, port, language and *Connect and play*.
3. A local sandbox save is created and kept in sync with the server. The MultiKerbal button in the app launcher opens players, vessels and chat.

Identity is the player name: while a vessel's owner is offline, somebody else could join under that name. The server password is the only protection for now.

## Two players on one PC

`E:\KSP-Dev2` is a second instance linked (directory junctions and hard links) to `E:\KSP-Dev`: same game and mods, but its own `settings.cfg`, saves, logs and player name (`PluginData/MultiKerbal/settings.cfg`).

**Start them one at a time:** launch the second one once the first has reached the main menu. While loading, KSP opens the files in `GameData` exclusively; if both load at once, part models fail (`IOException: Sharing violation` in `KSP.log`) and one instance can hang.
