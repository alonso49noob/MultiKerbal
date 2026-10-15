# Letting other players reach your server

*[Versión en español](HOSTING.es.md)*

The MultiKerbal server listens on port **6750**, over **both TCP and UDP** (change it with `Port` in `server.json`). TCP carries the important things: joining, chat and vessels. UDP carries in-flight positions, which are sent many times per second. If UDP doesn't get through, the game keeps working and sends everything over TCP, but other players' vessels move in jumps.

**The easiest way is the code**: you don't touch the router or install anything, and it works even behind CGNAT. If that doesn't suit you, there are four alternatives using an IP. Pick one:

> **From the game:** in KSP's main menu, the **Host** button in the MultiKerbal window shows your code and whether people join directly or through the relay, and lets you copy it. Its **Advanced** section has a tab for each IP alternative: it opens the router port by itself (if the router has UPnP), warns you about CGNAT, finds your Tailscale, ZeroTier, Radmin VPN or Hamachi IP and remembers your tunnel address. This guide covers what the game can't do by itself.

| Option | Who installs something | Router changes | Latency | When to use it |
|---|---|---|---|---|
| [Code (relay)](#code-the-easiest-way) | Nobody | No | Low, a bit more through the relay | **The easiest.** Almost always. |
| [Virtual LAN](#1-virtual-lan-tailscale-zerotier-radmin-vpn) | Everyone | No | Low | Playing with a few friends without depending on the relay. |
| [Tunnel](#2-tunnel-playitgg) | Only you | No | A bit higher | An address anyone can use without installing anything. |
| [Port forwarding](#3-port-forwarding-on-your-router) | Nobody | Yes | Lowest | Your ISP lets you open ports (no CGNAT). |
| [Cloud server](#4-cloud-server-vps) | Nobody | No | Low | A server that stays up 24/7 even when your PC is off. |

Whichever you choose, first start the server and check that you can join it yourself at `127.0.0.1`.

---

## Code: the easiest way

On start-up your server connects by itself to a **relay** (a central computer run by the project, always on) and gets a 6-character **code**, like `K7M-Q2P`. It is yours for good: it stays the same when you stop and start the server again.

1. Start the server and check that you can join at `127.0.0.1`.
2. See your code: type `relay` in the server console, or open **Host** in the game and press *Copy*.
3. Give it to your friends. In the MultiKerbal window they type it into the *Server (IP or code)* field however they like (`k7m q2p` works the same as `K7M-Q2P`), and that's it.

No ports to open and nothing to install. There are two ways for the player to reach you, and the mod picks by itself:

- **Direct:** if your server can be reached from the Internet (you have the port open), the player connects straight to you. The relay only gave them your address. The game shows "Directly reachable".
- **Through the relay:** if it can't (for example, behind CGNAT), the traffic goes through the relay. It works the same, with a bit more latency. The game shows "Through the relay".

If you want your server to appear in the game's **public list**, type `public on` in the server console (or tick *Show in the public list* in **Host**). By default it doesn't. `public off` removes it.

**About privacy:** MultiKerbal traffic is **not encrypted**, neither by IP nor through the relay. If a player joins through the relay, whoever runs it could see that traffic. With the other options in this guide (direct IP) the relay sees nothing of the game. More details, and how to run your own relay, in [RELAY.md](RELAY.md) (in Spanish).

**If there's no relay or it doesn't answer:** the server works the same and can be joined by IP with any of the options below. As soon as the relay is back, your server registers by itself and keeps its code. The `Relay`, `RelayAddress` and `Public` settings are in the [README](../README.md#server-settings). With `"Relay": false` in `server.json` the server doesn't use the relay.

---

## 1. Virtual LAN (Tailscale, ZeroTier, Radmin VPN)

A program creates a private network between your PCs, as if you were all in the same house. The game doesn't care that your friend is in another country.

With **Tailscale** (free for up to 100 devices):

1. You and each friend install Tailscale from [tailscale.com/download](https://tailscale.com/download).
2. You sign in with your account (Google, Microsoft or GitHub).
3. Invite your friends to your network: in the [Tailscale admin console](https://login.tailscale.com/admin/users), *Invite users*, or share just your PC with *Share* in the machines list.
4. On your PC, find your Tailscale IP: Tailscale icon in the taskbar → your device. It starts with `100.`.
5. Your friends type that IP into the MultiKerbal window, with port `6750`.

ZeroTier and Radmin VPN work the same way: everyone joins the same network and uses the IP the program gives your PC.

If it doesn't connect, check that Windows Firewall lets `MultiKerbal.Server.exe` through (see [Windows Firewall](#windows-firewall)).

---

## 2. Tunnel (playit.gg)

A program on your PC connects to playit.gg's servers, and they give you a public address that forwards everything to your server. Your friends install nothing, and it works even if your ISP won't let you open ports.

1. Download the agent from [playit.gg/download](https://playit.gg/download), install it and open it. It asks you to link it to an account on the website.
2. On the playit website, create a tunnel:
   - **Type: TCP+UDP.** It must be a single tunnel with both protocols. The mod uses the same port for TCP and UDP, so with two separate tunnels UDP wouldn't get through.
   - **Local address:** `127.0.0.1`, port `6750`.
3. playit gives you an address like `name.gl.joinmc.link` or `147.185.221.x` with a port, for example `:12345`.
4. Your friends type that address as the server and **that port** (not 6750) into the MultiKerbal window.

Keep the playit agent running while you play. The free plan adds some latency, because the data goes through playit's servers.

**ngrok won't do** on its free plan: it only carries TCP. The game would connect, but without UDP.

In the server log, every player shows up connecting from the same IP, the playit agent's. That's fine: the server doesn't use the IP to tell players apart.

---

## 3. Port forwarding on your router

You tell your router to send whatever arrives from the Internet on port 6750 to your PC.

### First: check you're not behind CGNAT

Many fibre providers and almost every mobile provider share one public IP between several customers (CGNAT). Behind CGNAT this option **cannot work**, whatever you do on your router.

1. Find the public IP the Internet sees: open [ifconfig.me](https://ifconfig.me).
2. Log into your router (usually `192.168.1.1` or `192.168.0.1` in the browser; the username and password are often on a sticker on the router) and find the **WAN IP** or **Internet IP**.
3. If both IPs match, go ahead. If they don't, or the router's starts with `100.64.` to `100.127.`, you're behind CGNAT: ask your ISP for a public IP (often free or cheap) or use option 1 or 2.

### Steps

1. **Give your PC a fixed address on your network.** On Windows, `ipconfig` → *IPv4 Address* (for example `192.168.1.20`). On the router, look for *DHCP reservation*, *IP reservation* or *static IP* and reserve that IP for your PC. Otherwise it changes one day and the forward points somewhere else.
2. **Create the forward.** On the router, look for *Port forwarding*, *NAT* or *Virtual servers*. Add a rule:
   - External port: `6750`
   - Internal port: `6750`
   - Internal IP: your PC's (`192.168.1.20` in the example)
   - Protocol: **TCP and UDP** (or "Both"). If the router only allows one per rule, create two rules.
3. **Allow the server through Windows Firewall** (see [below](#windows-firewall)).
4. Give your friends your public IP (the one from [ifconfig.me](https://ifconfig.me)) and port `6750`.

From inside your own home your public IP sometimes doesn't work (it depends on the router). To test it yourself, use `127.0.0.1`. To check that people can get in from outside, ask someone outside to try, or connect from your phone on mobile data, not WiFi.

### Your public IP changes

Most ISPs change your public IP from time to time. So you don't have to send it every time, create a free name on [DuckDNS](https://www.duckdns.org) (for example `mykerbal.duckdns.org`) and install its updater on your PC. Your friends use that name instead of the IP.

---

## 4. Cloud server (VPS)

You rent a computer in a data centre and run the server there. It stays up when your PC is off and nobody depends on your home connection.

The MultiKerbal server needs very little: 1 core and 1 GB of RAM is plenty for a handful of players.

- **Free:** [Oracle Cloud Free Tier](https://www.oracle.com/cloud/free/) (they ask for a card to verify, but don't charge).
- **Paid:** [Hetzner](https://www.hetzner.com/cloud), [DigitalOcean](https://www.digitalocean.com), [Vultr](https://www.vultr.com) and similar, around 4–6 € a month. Pick a data centre close to your players.

These steps are for an **Ubuntu** machine, the most common choice.

1. **Create the machine** in the provider's panel with Ubuntu and connect to it over SSH. From Windows, open PowerShell and type:
   ```bash
   ssh ubuntu@YOUR_SERVER_IP
   ```
   (the user may be `ubuntu` or `root` depending on the provider; the panel tells you).
2. **Install the .NET 10 Runtime** following [Microsoft's Linux guide](https://learn.microsoft.com/dotnet/core/install/linux). Check it's installed with `dotnet --list-runtimes`.
3. **Upload the server.** From PowerShell on your PC, in the folder with the zip:
   ```bash
   scp MultiKerbal-Server-x.y.z.zip ubuntu@YOUR_SERVER_IP:
   ```
   And on the server:
   ```bash
   sudo apt install unzip
   unzip MultiKerbal-Server-x.y.z.zip
   ```
4. **Open the port**, in two places:
   - In the **provider's panel**: look for *Firewall*, *Security group* or *Security list* (on Oracle) and allow incoming traffic on port `6750` over **TCP and UDP**.
   - **Inside the machine**, if it uses `ufw`:
     ```bash
     sudo ufw allow 6750/tcp
     sudo ufw allow 6750/udp
     ```
     Oracle's Ubuntu images also ship `iptables` rules that block everything; see Oracle's help on "opening ports on an instance".
5. **Try it** by hand:
   ```bash
   cd MultiKerbal-Server
   dotnet MultiKerbal.Server.dll
   ```
   Connect from KSP with the server's public IP and port `6750`. Type `stop` to close it.
6. **Keep it running** with a `systemd` service, which starts it when the machine boots and restarts it if it crashes. Create the file:
   ```bash
   sudo nano /etc/systemd/system/multikerbal.service
   ```
   with this (change `ubuntu` and the path if your user is different):
   ```ini
   [Unit]
   Description=MultiKerbal server
   After=network-online.target
   Wants=network-online.target

   [Service]
   User=ubuntu
   WorkingDirectory=/home/ubuntu/MultiKerbal-Server
   ExecStart=/usr/bin/dotnet MultiKerbal.Server.dll
   KillSignal=SIGINT
   TimeoutStopSec=30
   Restart=on-failure

   [Install]
   WantedBy=multi-user.target
   ```
   `KillSignal=SIGINT` makes stopping it behave like Ctrl+C: the server saves the universe before it exits. If `dotnet` isn't in `/usr/bin`, find it with `which dotnet`.

   Enable it:
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable --now multikerbal
   ```
   Useful commands:
   - `sudo systemctl status multikerbal`: whether it's running.
   - `journalctl -u multikerbal -f`: follow the log live (Ctrl+C to leave).
   - `sudo systemctl restart multikerbal`: restart it, for example after editing `server.json`.
   - `sudo systemctl stop multikerbal`: stop it.

   As a service there's no console, so you can't type commands like `kick` or `say`. If you need them, run it inside `tmux` instead of the service (`tmux`, then `dotnet MultiKerbal.Server.dll`, then Ctrl+B followed by D to leave it in the background; `tmux attach` to come back).

Back up the `Universe` folder now and then, for example by downloading it with `scp -r ubuntu@YOUR_SERVER_IP:MultiKerbal-Server/Universe .`

---

## Windows Firewall

The first time you open `MultiKerbal.Server.exe`, Windows asks whether to allow it. Tick **Private networks** (and **Public** if your network is marked public) and click *Allow*.

If you refused or the question never appeared, open PowerShell **as administrator** and type:

```powershell
New-NetFirewallRule -DisplayName "MultiKerbal TCP" -Direction Inbound -Protocol TCP -LocalPort 6750 -Action Allow
```

```powershell
New-NetFirewallRule -DisplayName "MultiKerbal UDP" -Direction Inbound -Protocol UDP -LocalPort 6750 -Action Allow
```

## If it won't connect

- **It sits on "Connecting..." and ends up "Disconnected":** TCP isn't getting through. Check in order: the server is running, you can join at `127.0.0.1`, Windows Firewall, then the router, the tunnel or the cloud firewall.
- **With a code, "Cannot reach the relay":** the relay is down or your connection can't reach it. In the meantime you can join by IP with any of the options above.
- **With a code, "There is no server running with that code":** the server is off, or the code is mistyped. Type `relay` in its console to see the right one.
- **You get in but other vessels move in jumps:** TCP gets through but UDP doesn't. Check that the forward, the tunnel or the firewall includes **UDP**, not just TCP.
- **The server console** prints a `Conexión #N desde ...` line (Spanish for "Connection #N from ...") every time someone arrives. If nothing shows up when someone tries to join, the problem is before the server (router, tunnel or firewall).
