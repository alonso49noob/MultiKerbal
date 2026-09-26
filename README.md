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

## Installation

You need two things: **the mod**, which every player installs in KSP, and **the server**, which only one person runs. They are separate downloads on the [Releases page](https://github.com/alonso49noob/MultiKerbal/releases):

| Release | File | For |
|---|---|---|
| **MultiKerbal** *x.y.z* | `MultiKerbal-x.y.z-mod.zip` | Every player (client side). |
| **MultiKerbal Server** *x.y.z* | `MultiKerbal-Server-x.y.z.zip` | Whoever hosts the game (server side). |

Everyone, the server included, should use the same version number.

### 1. The mod (every player)

**By hand:**

1. Download `MultiKerbal-<version>-mod.zip`.
2. Unzip it. You get a single folder, `MultiKerbal`.
3. Move that folder into the `GameData` folder of your KSP installation (next to the `Squad` folder):

   ```
   Kerbal Space Program/
   └── GameData/
       ├── Squad/
       └── MultiKerbal/          ← the folder from the zip
           ├── LICENSE
           ├── MultiKerbal.version
           ├── README.md
           └── Plugins/
               ├── MultiKerbal.Client.dll
               └── MultiKerbal.Common.dll
   ```

4. Start KSP. A **MultiKerbal** window appears in the main menu.

**With CKAN:** the mod has been [submitted](https://github.com/KSP-CKAN/NetKAN/pull/11575) and is waiting for review. Once it is listed, search for *MultiKerbal* in CKAN and install it like any other mod.

The mod needs no other mods. It only works with KSP 1.12.x.

### 2. The server (one person)

1. Install the **[.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)** (the "Runtime" download is enough, you do not need the SDK).
2. Download `MultiKerbal-Server-<version>.zip` from the **MultiKerbal Server** release and unzip it anywhere you like, **not** inside KSP. You get a folder, `MultiKerbal-Server`.
3. Start it from that folder:
   - **Windows:** double-click `MultiKerbal.Server.exe`. If Windows Firewall asks, allow it.
   - **Linux / macOS:** run `dotnet MultiKerbal.Server.dll`.
4. The first run creates `server.json` next to the server. Close the server (type `stop`), set a name and a password in that file if you want them, and start it again. See [Server settings](#server-settings).
5. Tell the other players the address to use:
   - **Same PC:** `127.0.0.1`
   - **Same home network:** your local IP (on Windows, `ipconfig` → *IPv4 Address*, e.g. `192.168.1.20`).
   - **Over the Internet:** your public IP, and on your router forward port **6750** for both **TCP and UDP** to the PC running the server.

The universe is saved in the `Universe` folder next to the server, every minute and whenever you stop it with `stop` or Ctrl+C. Back that folder up if you care about it.

### 3. Connect

In KSP's main menu, fill in the MultiKerbal window: your name, the server address, the port (`6750` unless you changed it) and the password if there is one. Press **Connect and play**.

If your mods do not match the server's, a list shows what is missing before the game starts, and you choose whether to join anyway.

### Updating and uninstalling

- **Update:** delete the old `GameData/MultiKerbal` folder and move the new one in. Update the server at the same time: a server and a mod from different versions may refuse each other. To update the server, replace its files but keep `server.json` and the `Universe` folder.
- **Uninstall:** delete `GameData/MultiKerbal`. MultiKerbal keeps its local copy of the game in `saves/MultiKerbal` and its settings in `PluginData/MultiKerbal`; you can delete both. Your other saves are never touched.

## Playing

1. Start the server.
2. Connect from KSP's main menu.
3. A local sandbox save is created and kept in sync with the server. The MultiKerbal button in the app launcher opens players, vessels and chat.

**Warp** (the *Warp* button in the MultiKerbal window):

- *Accept automatically* the warp other players ask for, up to a maximum, only when everything you tick is true: not flying, vessel in orbit or landed, engines off, no other vessels within 2.5 km.
- *Accept while I am away*: after the time you choose without touching the keyboard or mouse (30 s to 10 min), warp is accepted up to the same maximum, ignoring the other conditions.
- *Refuse automatically* when any of the ticked conditions is true. With nothing ticked it always refuses, even in the editor. While it is on, nothing is accepted automatically.
- Physics warp (x2 to x4) is never accepted automatically.

**Vessels:** open *Vessel* next to any vessel in the list to see its owner and access, give it away, hand it over, ask for it, watch it or pick a co-pilot. *Your new vessels* sets the access your launches start with.

Identity is the player name: while a vessel's owner is offline, somebody else could join under that name. The server password is the only protection for now.

## Server settings

`server.json`:

| Setting | Default | What it does |
|---|---|---|
| `ServerName` | `Servidor MultiKerbal` | Name shown to players. |
| `Port` | `6750` | TCP and UDP port. |
| `MaxPlayers` | `8` | Players allowed at once. |
| `Password` | *(empty)* | Empty means no password. |
| `Motd` | welcome text | Message shown in the chat when someone joins. |
| `PauseClockWhenEmpty` | `true` | Stop the universe clock while nobody is connected. |
| `ModPolicy` | `warn` | `off` ignores mods, `warn` lets everyone in and announces differences, `strict` refuses players whose mods differ. |
| `Language` | `es` | Language of the chat messages and refusal reasons the server sends: `es` or `en`. |
| `DataDirectory` | `Universe` | Where the universe is stored. |

Console commands: `list`, `vessels`, `owner <vessel> <player|nobody> [private|shared|public]`, `mods [player|set <player>]`, `warp`, `say <text>`, `kick <player> [reason]`, `time`, `save`, `stop`. Type `help` to see them in the console.

## Building from source

| Project | Target | What it is |
|---|---|---|
| `src/MultiKerbal.Common` | `net472` + `net10.0` | Protocol, serialization, TCP/UDP transport and time logic. No dependencies. |
| `src/MultiKerbal.Server` | `net10.0` | Dedicated console server. Authority over the universe. |
| `src/MultiKerbal.Client` | `net472` | KSP plugin (copied to `GameData/MultiKerbal/Plugins`). |
| `tests/MultiKerbal.Tests` | `net10.0` | Unit tests plus real client-server integration tests. |

The mod ships no dependencies: KSP's Mono has no `netstandard.dll`, so the transport is written from scratch.

You need the .NET SDK 10 and KSP 1.12.5. For development a clean copy (Squad/DLC only) is recommended, e.g. `E:\KSP-Dev`.

1. Copy `LocalDev.props.example` to `LocalDev.props` and point `KSPDir` at your KSP folder.
2. Build and run the tests:

```bash
dotnet build MultiKerbal.sln
```

```bash
dotnet test MultiKerbal.sln
```

With `DeployToKSP=true`, every client build copies the mod into `$(KSPDir)\GameData\MultiKerbal`. To run the server from source:

```bash
dotnet run --project src/MultiKerbal.Server -- --port 6750
```

To package a release, bump `<Version>` in `Directory.Build.props`, commit, and run the script below. It builds from the last commit (uncommitted changes are left out) and writes both zips to `dist/`: the mod one for the **MultiKerbal** release, the server one for the **MultiKerbal Server** release.

```bash
powershell -ExecutionPolicy Bypass -File tools/Package.ps1
```

### Two players on one PC

`E:\KSP-Dev2` is a second instance linked (directory junctions and hard links) to `E:\KSP-Dev`: same game and mods, but its own `settings.cfg`, saves, logs and player name (`PluginData/MultiKerbal/settings.cfg`).

**Start them one at a time:** launch the second one once the first has reached the main menu. While loading, KSP opens the files in `GameData` exclusively; if both load at once, part models fail (`IOException: Sharing violation` in `KSP.log`) and one instance can hang.

## License

MIT, see [LICENSE](LICENSE). Bug reports and pull requests are welcome.
