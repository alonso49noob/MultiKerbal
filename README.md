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
- **Shared control.** Whoever flies a vessel can name another player co-pilot: the co-pilot's controls travel over the network and add to the pilot's. You can also ask for control of someone else's vessel.
- **Join with a code.** Whoever hosts gets a short code (`K7M-Q2P`) and friends type it instead of an IP: no router changes, and it works even behind CGNAT. If the server can be reached directly, players connect directly; if not, traffic goes through a *relay*.
- **Public list.** Servers that want to can appear in an in-game list (the **List** button), showing players, whether they need a key, version and connection type.
- **Mod checking.** The server remembers the mod list of the first player who joins. Before you enter the game you get the list compared with yours, green for what matches and red for what is missing or extra.

## Installation

You need two things: **the mod**, which every player installs in KSP, and **the server**, which only one person runs. They are separate downloads on the [Releases page](https://github.com/alonso49noob/MultiKerbal/releases):

| Release | File | For |
|---|---|---|
| **MultiKerbal** *x.y.z* | `MultiKerbal-x.y.z-mod.zip` | Every player (client side). |
| **MultiKerbal Server** *x.y.z* | `MultiKerbal-Server-x.y.z.zip` | Whoever hosts the game (server side). |

Everyone, the server included, should use the same version number.

> **Important:** **0.2.0** is not compatible with **0.1.x**. A 0.1.x mod cannot join a 0.2.0 server, nor the other way round (the protocol went from version 5 to 6): whoever tries gets the incompatible-version notice. Update the mod and the server together.

### 1. The mod (every player)

**By hand:**

1. Download `MultiKerbal-<version>-mod.zip` from the [Releases page](https://github.com/alonso49noob/MultiKerbal/releases) or from [SpaceDock](https://spacedock.info/mod/4611/MultiKerbal) (same file).
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
4. The first run creates `server.json` next to the server. Close the server (type `stop`), set a name and a server key in that file if you want them, and start it again. See [Server settings](#server-settings).
5. Tell the other players the address to use:
   - **Same PC:** `127.0.0.1`
   - **Same home network:** your local IP (on Windows, `ipconfig` → *IPv4 Address*, e.g. `192.168.1.20`).
   - **Over the Internet, with the code:** on start-up the server registers with the relay and shows a code like `K7M-Q2P` (type `relay` in its console to see it, or look at the **Host** button in the game). Give it to your friends: no ports to open and nothing else to install. This is the easiest way. The code is yours as long as you keep `relay.key` (in the `Universe` folder). **Never share or upload `relay.key`** (to GitHub, a forum, a copy of the universe you give someone...): whoever has it can take over your code.
   - **Over the Internet, with an IP:** there are four other ways: a virtual LAN (Tailscale), a tunnel (playit.gg), forwarding port **6750** (TCP and UDP) on your router, or a cloud server. [docs/HOSTING.md](docs/HOSTING.md) walks through each one.

If you want your server to appear in the public list, type `public on` in its console (or tick *Show in the public list* in **Host**). By default it does not: only people with the code get in.

**Relay limits:** at most 4 players from the same public IP can be in the same server through the relay at once (for example, 4 friends in the same house), and at most 3 new servers (with a new code) can be created from the same IP per day. If you need more, open the port so they connect directly. Details in [docs/RELAY.md](docs/RELAY.md#límites) (in Spanish).

> **If you use a tunnel on your PC** (playit.gg or a similar agent): connections that come through the tunnel look as if they were made from your own PC, so anyone who gets in through it could toggle *Show in the public list* or ask for another check, just like you can from **Host**. If that matters to you, use the code or open the port instead of the tunnel.

> **Not encrypted:** game traffic is not encrypted, neither by IP nor through the relay. When a player joins *through the relay*, whoever runs the relay could see that traffic. If the server can be reached directly, players connect directly and it does not go through the relay. More in [docs/RELAY.md](docs/RELAY.md) (in Spanish), which also explains how to run your own relay.

The universe is saved in the `Universe` folder next to the server, every minute and whenever you stop it with `stop` or Ctrl+C. Back that folder up if you care about it.

### 3. Connect

In KSP's main menu, fill in the MultiKerbal window: your name and the server, which can be:

- **A code** (`K7M-Q2P`): type it however you like, `k7m q2p` and `K7MQ2P` work the same. No port needed. A 6-character computer name made of letters and digits, like `pc2024`, is also read as a code: type it with the port, `pc2024:6750`.
- **An IP or a name** (paste `address:port` in one go and the port fills itself in), with the port (`6750` unless you changed it).
- **One from the list:** the **List** button opens the public servers; pick one and press *Join*. If it has a key, it asks you to type it.

Add the server key if it has one and press **Connect and play**. With a code, if the server can be reached directly you connect directly; if not, you go through the relay without doing anything.

**Your password** is your player account's, separate from the server key. The first time you join with a name, that name is registered with the password you type (at least 4 characters); after that only someone who knows it gets in. If you forget it, the admin can delete your account (`account delete <name>`) and you pick a new one next time. *Remember it* stores it on this computer.

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

**Vessels:** open *Vessel* next to any vessel in the list to see its owner and access, give it away, hand it over, ask for it or pick a co-pilot. *Your new vessels* sets the access your launches start with.

**Watch:** the *Watch* button next to a vessel you are not flying takes the camera to it, wherever it is, with the controls locked. If it is far away, the flight scene loads around it; if you were flying, your vessel must be stable first: landed and still, or in orbit with the engines off. *Stop watching* takes you back to your vessel or to the scene you came from.

**Lending:** the owner can lend a vessel (or a kerbal on EVA) to a player, who can fly it even if private but cannot destroy, recover or delete it, and take it back at any time. Destroying, recovering or deleting someone else's vessel needs its owner's permission (public access) even while you fly it; if you crash it, it goes back to how its owner left it. A kerbal can't be aboard two players' vessels at once.

**Shared progress (experimental):** with `GameMode` set to `science` or `career`, science, technologies and experiments already done are shared by everyone. In career also funds, reputation, purchased parts, facility upgrades and contracts: offers are per player, but an accepted contract is everyone's and whoever completes it first gets paid. In any mode, a kerbal hired or killed is so for everyone.

Stages dropped from someone else's vessel stay the owner's (you can still lose them). If a vessel comes back damaged, its owner can *Restore it to how you left it* while nobody flies it.

## Server settings

`server.json`:

| Setting | Default | What it does |
|---|---|---|
| `ServerName` | `Servidor MultiKerbal` | Name shown to players. |
| `Port` | `6750` | TCP and UDP port. |
| `MaxPlayers` | `8` | Players allowed at once. |
| `Password` | *(empty)* | Server key, the same for everyone. Empty means none. Each player also has their own account password. |
| `Motd` | welcome text | Message shown in the chat when someone joins. |
| `PauseClockWhenEmpty` | `true` | Stop the universe clock while nobody is connected. |
| `ModPolicy` | `warn` | `off` ignores mods, `warn` lets everyone in and announces differences, `strict` refuses players whose mods differ. |
| `Language` | `es` | Language of the chat messages and refusal reasons the server sends: `es` or `en`. |
| `GameMode` | `sandbox` | `sandbox` (no progress), `science` (shared science and technologies) or `career` (plus funds and reputation). |
| `StartingFunds` | `25000` | Funds a new career starts with. |
| `Relay` | `true` | Register with the relay: gives a code to join without opening ports and allows appearing in the public list. With `false` the server can only be joined by IP. |
| `RelayAddress` | `multikerbal.duckdns.org:6760` | Relay address (`name` or `name:port`). Change it only if you use another relay; see [docs/RELAY.md](docs/RELAY.md) (in Spanish). |
| `Public` | `false` | Appear in the relay's public server list. With `false`, only people with the code or the IP get in. |
| `DataDirectory` | `Universe` | Where the universe is stored. |

Console commands: `list`, `vessels`, `owner <vessel> <player|nobody> [private|shared|public]`, `account` (accounts) and `account delete <player>` (forgotten password), `progress`, `mods [player|set <player>]`, `warp`, `say <text>`, `relay` (code, and whether players join directly or through the relay), `public [on|off]` (appear in the public list or not; saved to `server.json`), `kick <player> [reason]`, `time`, `save`, `stop`. Type `help` to see them in the console.

## Building from source

| Project | Target | What it is |
|---|---|---|
| `src/MultiKerbal.Common` | `net472` + `net10.0` | Protocol, serialization, TCP/UDP transport and time logic. No dependencies. |
| `src/MultiKerbal.Server` | `net10.0` | Dedicated console server. Authority over the universe. |
| `src/MultiKerbal.Relay` | `net10.0` | Console relay: hands out the codes, serves the public list and carries the traffic of servers that can't be reached directly. See [docs/RELAY.md](docs/RELAY.md) (in Spanish). |
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

The relay is published separately, as a single executable that needs no .NET installed (one for `linux-arm64` and one for `win-x64`, each with its own zip, in `dist/`):

```bash
powershell -ExecutionPolicy Bypass -File tools/PublishRelay.ps1
```

### Two players on one PC

`E:\KSP-Dev2` is a second instance linked (directory junctions and hard links) to `E:\KSP-Dev`: same game and mods, but its own `settings.cfg`, saves, logs and player name (`PluginData/MultiKerbal/settings.cfg`).

**Start them one at a time:** launch the second one once the first has reached the main menu. While loading, KSP opens the files in `GameData` exclusively; if both load at once, part models fail (`IOException: Sharing violation` in `KSP.log`) and one instance can hang.

## License

MIT, see [LICENSE](LICENSE). Bug reports and pull requests are welcome.
