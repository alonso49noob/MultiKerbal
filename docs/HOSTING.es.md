# Cómo dejar que otros entren a tu servidor

*[English version](HOSTING.md)*

El servidor de MultiKerbal escucha en el puerto **6750**, por **TCP y UDP** a la vez (se cambia con `Port` en `server.json`). TCP lleva lo importante: entrar, chat y naves. UDP lleva las posiciones en vuelo, que se mandan muchas veces por segundo. Si el UDP no llega, el juego sigue funcionando y lo manda todo por TCP, pero las naves de los demás se mueven a saltos.

**La forma más fácil es el código**: no tocas el router ni instalas nada, y funciona aunque tengas CGNAT. Si no te sirve, hay cuatro alternativas con IP. Elige una:

> **Desde el juego:** en el menú principal de KSP, el botón **Hospedar** de la ventana MultiKerbal te enseña tu código y si se entra directo o por el relay, y deja copiarlo. En su apartado **Avanzado** hay una pestaña para cada alternativa con IP: abre el puerto del router solo (si tiene UPnP), avisa si tienes CGNAT, encuentra tu IP de Tailscale, ZeroTier, Radmin VPN o Hamachi y guarda la dirección de tu túnel. Esta guía es para lo que el juego no puede hacer solo.

| Opción | Quién instala algo | Tocar el router | Retraso | Cuándo usarla |
|---|---|---|---|---|
| [Código (relay)](#código-la-forma-más-fácil) | Nadie | No | Bajo, algo más si va por el relay | **La más fácil.** Casi siempre. |
| [Red local virtual](#1-red-local-virtual-tailscale-zerotier-radmin-vpn) | Todos | No | Bajo | Jugar con unos pocos amigos sin depender del relay. |
| [Túnel](#2-túnel-playitgg) | Solo tú | No | Algo más alto | Una dirección que cualquiera pueda usar sin instalar nada. |
| [Abrir el puerto](#3-abrir-el-puerto-en-el-router-port-forwarding) | Nadie | Sí | El más bajo | Tu operador te deja abrir puertos (sin CGNAT). |
| [Servidor en la nube](#4-servidor-en-la-nube-vps) | Nadie | No | Bajo | Servidor encendido 24 h aunque apagues tu PC. |

En todas, primero arranca el servidor y comprueba que tú mismo puedes entrar con la dirección `127.0.0.1`.

---

## Código: la forma más fácil

Al arrancar, tu servidor se conecta solo a un **relay** (un ordenador central del proyecto, siempre encendido) y recibe un **código** de 6 caracteres, como `K7M-Q2P`. Es tuyo mientras conserves `relay.key` (en la carpeta `Universe` del servidor): sigue siendo el mismo aunque apagues y vuelvas a encender el servidor.

> **No compartas ni subas nunca `relay.key`** (a GitHub, a un foro, en una copia del universo que pases a alguien...): quien tenga ese archivo puede registrarse con tu código y quedarse con él. Si mueves el servidor a otro PC, llévate la carpeta `Universe` entera y conservarás el código.

1. Arranca el servidor y comprueba que tú entras con `127.0.0.1`.
2. Mira tu código: escribe `relay` en la consola del servidor, o abre **Hospedar** en el juego y pulsa *Copiar*.
3. Pásalo a tus amigos. En la ventana de MultiKerbal lo escriben en el campo *Servidor (IP o código)*, como quieran (`k7m q2p` vale igual que `K7M-Q2P`), y ya está.

No hay que abrir puertos ni instalar nada. Hay dos formas de que el jugador llegue a ti, y el mod elige solo:

- **Directa:** si tu servidor es accesible desde Internet (tienes el puerto abierto), el jugador se conecta directamente a ti. El relay solo le dio tu dirección. El juego lo marca como «Accesible directamente».
- **Por el relay:** si no lo es (por ejemplo, con CGNAT), el tráfico pasa por el relay. Funciona igual, con algo más de retraso. El juego lo marca como «Por el relay».

Si quieres que tu servidor salga en la **lista pública** del juego, escribe `public on` en la consola del servidor (o marca *Mostrar en la lista pública* en **Hospedar**). Por defecto no sale. `public off` lo quita.

**Sobre la privacidad:** el tráfico de MultiKerbal **no va cifrado**, ni por IP ni por el relay. Si un jugador entra por el relay, quien lo administra podría ver ese tráfico. Con las demás opciones de esta guía (IP directa) el relay no ve nada de la partida. Más detalles, y cómo montar tu propio relay, en [RELAY.md](RELAY.md).

**Si no hay relay o no contesta:** el servidor funciona igual y se puede entrar por IP con cualquiera de las opciones de abajo. En cuanto el relay vuelve, tu servidor se registra solo y conserva su código. Los ajustes `Relay`, `RelayAddress` y `Public` están en el [README](../README.es.md#ajustes-del-servidor). Con `"Relay": false` en `server.json` el servidor no usa el relay.

---

## 1. Red local virtual (Tailscale, ZeroTier, Radmin VPN)

Un programa crea una red privada entre vuestros PCs, como si estuvierais en la misma casa. Al juego le da igual que esté en otro país.

Con **Tailscale** (gratis hasta 100 dispositivos):

1. Tú y cada amigo instaláis Tailscale desde [tailscale.com/download](https://tailscale.com/download).
2. Tú entras con tu cuenta (Google, Microsoft o GitHub).
3. Invita a tus amigos a tu red: en el [panel de Tailscale](https://login.tailscale.com/admin/users), *Invite users*, o comparte solo tu PC con *Share* en la lista de máquinas.
4. En tu PC, mira tu IP de Tailscale: icono de Tailscale en la barra de tareas → tu dispositivo. Empieza por `100.`.
5. Tus amigos ponen esa IP en la ventana de MultiKerbal, con el puerto `6750`.

ZeroTier y Radmin VPN funcionan igual: todos entran en la misma red y usan la IP que el programa da a tu PC.

Si no conecta, revisa que el Firewall de Windows deje pasar a `MultiKerbal.Server.exe` (ver [Firewall de Windows](#firewall-de-windows)).

---

## 2. Túnel (playit.gg)

Un programa en tu PC se conecta a los servidores de playit.gg, y ellos te dan una dirección pública que reenvía todo a tu servidor. Tus amigos no instalan nada, y funciona aunque tu operador no te deje abrir puertos.

1. Descarga el agente de [playit.gg/download](https://playit.gg/download), instálalo y ábrelo. Te pide enlazarlo con una cuenta en la web.
2. En la web de playit, crea un túnel:
   - **Tipo: TCP+UDP.** Tiene que ser uno solo con los dos protocolos. El mod usa el mismo puerto para TCP y para UDP, así que con dos túneles separados el UDP no llegaría.
   - **Dirección local:** `127.0.0.1`, puerto `6750`.
3. playit te da una dirección tipo `nombre.gl.joinmc.link` o `147.185.221.x` con un puerto, por ejemplo `:12345`.
4. Tus amigos ponen esa dirección como servidor y **ese puerto** (no el 6750) en la ventana de MultiKerbal.

Deja el agente de playit abierto mientras jugáis. El plan gratis añade algo de retraso, porque los datos pasan por los servidores de playit.

**ngrok no sirve** en su plan gratis: solo pasa TCP. El juego entraría, pero sin UDP.

En el registro del servidor, todos los jugadores aparecen conectando desde la misma IP, la del agente de playit. Es normal, el servidor no usa la IP para distinguir a los jugadores.

> **Ojo con Hospedar:** como el agente corre en tu PC, el servidor cree que todo lo que llega por el túnel viene de tu propio equipo. Por eso cualquiera que entre por la dirección de playit podría hacer lo mismo que tú desde **Hospedar**: cambiar *Mostrar en la lista pública* (y sacar a la lista un servidor que querías privado) o pedir otra comprobación de acceso. Si eso te importa, usa el código o abre el puerto en vez del túnel. Lo mismo pasa con cualquier otro agente de túnel que corra en el mismo PC que el servidor.

---

## 3. Abrir el puerto en el router (port forwarding)

Le dices a tu router que lo que llegue de Internet al puerto 6750 lo mande a tu PC.

### Antes: comprueba que no tienes CGNAT

Muchos operadores de fibra y casi todos los de móvil comparten una IP pública entre varios clientes (CGNAT). Con CGNAT esta opción **no puede funcionar**, hagas lo que hagas en el router.

1. Busca la IP pública que ve Internet: entra en [ifconfig.me](https://ifconfig.me).
2. Entra en tu router (normalmente `192.168.1.1` o `192.168.0.1` en el navegador; el usuario y la contraseña suelen venir en una pegatina del router) y busca la **IP WAN** o **IP de Internet**.
3. Si las dos IP son iguales, adelante. Si no coinciden, o la del router empieza por `100.64.` a `100.127.`, tienes CGNAT: pide a tu operador una IP pública (a veces lo llaman "sacarte del CGNAT"; suele ser gratis o barato) o usa la opción 1 o la 2.

### Pasos

1. **Dale una IP fija a tu PC en tu red.** En Windows, `ipconfig` → *Dirección IPv4* (por ejemplo `192.168.1.20`). En el router, busca *DHCP*, *Reserva de IP* o *IP estática* y reserva esa IP para tu PC. Si no, un día cambia y la redirección apunta a otro sitio.
2. **Crea la redirección.** En el router, busca *Redirección de puertos*, *Port forwarding*, *NAT* o *Servidores virtuales*. Añade una regla:
   - Puerto externo: `6750`
   - Puerto interno: `6750`
   - IP interna: la de tu PC (`192.168.1.20` en el ejemplo)
   - Protocolo: **TCP y UDP** (o "Ambos"). Si el router solo deja uno por regla, crea dos reglas.
3. **Permite el servidor en el Firewall de Windows** (ver [abajo](#firewall-de-windows)).
4. Pasa a tus amigos tu IP pública (la de [ifconfig.me](https://ifconfig.me)) y el puerto `6750`.

Desde tu propia casa, tu IP pública a veces no funciona (depende del router). Para probar tú, usa `127.0.0.1`. Para comprobar que se puede entrar desde fuera, pide a alguien de fuera que lo intente, o conéctate desde el móvil con los datos, no con el WiFi.

### Tu IP pública cambia

Casi todos los operadores te cambian la IP pública de vez en cuando. Para no tener que pasarla cada vez, crea un nombre gratis en [DuckDNS](https://www.duckdns.org) (por ejemplo `mikerbal.duckdns.org`) e instala su programa de actualización en tu PC. Tus amigos usan ese nombre en vez de la IP.

---

## 4. Servidor en la nube (VPS)

Alquilas un ordenador en un centro de datos y ejecutas allí el servidor. Queda encendido aunque apagues tu PC y nadie depende de tu conexión de casa.

El servidor de MultiKerbal gasta muy poco: con 1 núcleo y 1 GB de RAM sobra para unos pocos jugadores.

- **Gratis:** [Oracle Cloud Free Tier](https://www.oracle.com/cloud/free/) (piden tarjeta para verificar, pero no cobran).
- **De pago:** [Hetzner](https://www.hetzner.com/cloud), [DigitalOcean](https://www.digitalocean.com), [Vultr](https://www.vultr.com) y similares, unos 4–6 € al mes. Elige un centro de datos cerca de tus jugadores.

Estos pasos son para una máquina con **Ubuntu**, que es lo más habitual.

1. **Crea la máquina** en el panel del proveedor con Ubuntu y conéctate a ella por SSH. Desde Windows, abre PowerShell y escribe:
   ```bash
   ssh ubuntu@IP_DE_TU_SERVIDOR
   ```
   (el usuario puede ser `ubuntu` o `root` según el proveedor; te lo dice el panel).
2. **Instala el .NET 10 Runtime** siguiendo la [guía de Microsoft para Linux](https://learn.microsoft.com/dotnet/core/install/linux). Comprueba que quedó instalado con `dotnet --list-runtimes`.
3. **Sube el servidor.** Desde PowerShell en tu PC, en la carpeta donde está el zip:
   ```bash
   scp MultiKerbal-Server-x.y.z.zip ubuntu@IP_DE_TU_SERVIDOR:
   ```
   Y en el servidor:
   ```bash
   sudo apt install unzip
   unzip MultiKerbal-Server-x.y.z.zip
   ```
4. **Abre el puerto**, en dos sitios:
   - En el **panel del proveedor**: busca *Firewall*, *Security group* o *Security list* (en Oracle) y permite la entrada al puerto `6750` por **TCP y UDP**.
   - **Dentro de la máquina**, si usa `ufw`:
     ```bash
     sudo ufw allow 6750/tcp
     sudo ufw allow 6750/udp
     ```
     Las imágenes de Ubuntu de Oracle traen además reglas de `iptables` que lo bloquean todo; mira la ayuda de Oracle sobre "abrir puertos en una instancia".
5. **Pruébalo** a mano:
   ```bash
   cd MultiKerbal-Server
   dotnet MultiKerbal.Server.dll
   ```
   Conéctate desde KSP con la IP pública del servidor y el puerto `6750`. Escribe `stop` para cerrarlo.
6. **Déjalo funcionando siempre** con un servicio de `systemd`, que lo arranca al encender la máquina y lo reinicia si se cae. Crea el archivo:
   ```bash
   sudo nano /etc/systemd/system/multikerbal.service
   ```
   con esto (cambia `ubuntu` y la ruta si tu usuario es otro):
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
   `KillSignal=SIGINT` hace que al pararlo se comporte como Ctrl+C: el servidor guarda el universo antes de cerrarse. Si `dotnet` no está en `/usr/bin`, mira dónde está con `which dotnet`.

   Actívalo:
   ```bash
   sudo systemctl daemon-reload
   sudo systemctl enable --now multikerbal
   ```
   Comandos útiles:
   - `sudo systemctl status multikerbal`: si está funcionando.
   - `journalctl -u multikerbal -f`: ver el registro en directo (Ctrl+C para salir).
   - `sudo systemctl restart multikerbal`: reiniciarlo, por ejemplo tras cambiar `server.json`.
   - `sudo systemctl stop multikerbal`: pararlo.

   Como servicio no hay consola, así que no puedes escribir comandos como `kick` o `say`. Si los necesitas, en vez del servicio arráncalo dentro de `tmux` (`tmux`, luego `dotnet MultiKerbal.Server.dll`, y Ctrl+B y luego D para dejarlo en segundo plano; `tmux attach` para volver).

Haz copia de la carpeta `Universe` de vez en cuando, por ejemplo bajándola con `scp -r ubuntu@IP_DE_TU_SERVIDOR:MultiKerbal-Server/Universe .`

---

## Firewall de Windows

La primera vez que abres `MultiKerbal.Server.exe`, Windows pregunta si lo permites. Marca **Redes privadas** (y **Públicas** si tu red está marcada como pública) y pulsa *Permitir*.

Si lo rechazaste o no salió la pregunta, abre PowerShell **como administrador** y escribe:

```powershell
New-NetFirewallRule -DisplayName "MultiKerbal TCP" -Direction Inbound -Protocol TCP -LocalPort 6750 -Action Allow
```

```powershell
New-NetFirewallRule -DisplayName "MultiKerbal UDP" -Direction Inbound -Protocol UDP -LocalPort 6750 -Action Allow
```

## Si no conecta

- **Se queda en "Conectando..." y acaba en "Desconectado":** el TCP no llega. Revisa por orden: que el servidor esté abierto, que tú entres con `127.0.0.1`, el Firewall de Windows y luego el router, el túnel o el firewall de la nube.
- **Con un código, «No se puede contactar con el relay»:** el relay está apagado o tu conexión no llega a él. Mientras, se puede entrar por IP con cualquiera de las opciones de arriba.
- **Con un código, «No hay ningún servidor encendido con ese código»:** el servidor está apagado, o el código está mal escrito. Escribe `relay` en su consola para ver el bueno.
- **Escribes un nombre de equipo de 6 letras y números (como `pc2024`) y dice que no hay servidor con ese código:** se ha tomado por un código. Escríbelo con el puerto: `pc2024:6750`.
- **Entra pero las naves de los demás van a saltos:** el TCP llega pero el UDP no. Revisa que la redirección, el túnel o el firewall incluyan **UDP**, no solo TCP.
- **En la consola del servidor** sale una línea `Conexión #N desde ...` cada vez que alguien llega. Si no sale nada al intentar entrar, el problema está antes del servidor (router, túnel o firewall).
