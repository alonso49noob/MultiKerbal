# El relay de MultiKerbal

Esta guía es para quien quiera **montar su propio relay**. Si solo quieres jugar o hospedar una partida, no la necesitas: el mod y el servidor ya apuntan al relay del proyecto por defecto (ver el [README](../README.es.md)).

## Qué es

El relay es un programa pequeño que se queda encendido en una máquina con IP pública (por ejemplo, una de Oracle Cloud). Hace tres cosas:

1. **Da un código** a cada servidor de MultiKerbal que se registra (`K7M-Q2P`, 6 caracteres). El código es permanente: el relay lo asocia a una clave secreta que el servidor guarda en `relay.key`, así que sobrevive a reinicios de los dos.
2. **Pasa el tráfico** entre un jugador y un servidor que no se pueden ver directamente (porque el servidor está detrás de un router sin puertos abiertos o de CGNAT). El servidor y el jugador se conectan *al relay*, y el relay copia los bytes de uno al otro, por TCP y por UDP.
3. **Lleva la lista pública**: los servidores que lo piden aparecen en la ventana *Servidores públicos* del mod.

Además comprueba si cada servidor es accesible directamente desde Internet. Si lo es, los jugadores que entran con código se conectan **directos** y el relay no pasa su tráfico; si no, entran por el relay.

El relay **no guarda el contenido** de lo que pasa por él. Su registro (`logs/`) lleva altas y bajas de servidores, sesiones, rechazos y cifras de tráfico.

> **El tráfico va sin cifrar.** Igual que cuando se entra por IP, nada de lo que viaja entre KSP y el servidor está cifrado. Quien administra el relay *podría ver* todo lo que pasa por él mientras un jugador entra por el relay. Por eso el relay del proyecto no debe usarse para nada que quieras mantener en secreto, y si montas uno para otros, debes decírselo a quienes lo usen. Si el servidor es accesible directamente, ese tráfico no pasa por el relay.

## Requisitos

- Una máquina con **IP pública** y puertos que puedas abrir. Basta **1 núcleo y 1 GB de RAM**. La gratuita [Oracle Cloud Free Tier](https://www.oracle.com/cloud/free/) (instancia Ampere, `linux-arm64`) sobra.
- Un nombre fijo que apunte a ella, por ejemplo gratis con [DuckDNS](https://www.duckdns.org). Se puede usar la IP directamente, pero si cambia tendrías que avisar a todos.
- **No hace falta instalar .NET**: el ejecutable lleva dentro todo lo que necesita.

## Obtener el programa

Cada versión trae un zip por sistema:

| Archivo | Para |
|---|---|
| `MultiKerbal-Relay-x.y.z-linux-arm64.zip` | Ubuntu en ARM (Oracle Ampere). |
| `MultiKerbal-Relay-x.y.z-win-x64.zip` | Windows, para probar. |

Para generarlos desde el código:

```bash
powershell -ExecutionPolicy Bypass -File tools/PublishRelay.ps1
```

Deja los dos ejecutables en `dist/relay-linux-arm64/MultiKerbal.Relay` y `dist/relay-win-x64/MultiKerbal.Relay.exe`, y los dos zips en `dist/`.

## Instalar en Ubuntu

1. **Crea la máquina** con Ubuntu y conéctate por SSH (`ssh ubuntu@IP_DE_TU_MAQUINA`).
2. **Sube y descomprime el zip.** Desde PowerShell en tu PC:
   ```bash
   scp MultiKerbal-Relay-x.y.z-linux-arm64.zip ubuntu@IP_DE_TU_MAQUINA:
   ```
   Y en la máquina:
   ```bash
   sudo apt install unzip
   unzip MultiKerbal-Relay-x.y.z-linux-arm64.zip
   cd MultiKerbal-Relay
   chmod +x MultiKerbal.Relay
   ```
   (El zip no guarda el permiso de ejecución; por eso el `chmod`.)
3. **Abre los puertos** (ver [Puertos](#puertos)).
4. **Pruébalo a mano:**
   ```bash
   ./MultiKerbal.Relay
   ```
   Debe decir `Relay escuchando en el puerto 6760 (TCP y UDP)...`. Escribe `stop` para cerrarlo.
5. **Déjalo funcionando siempre** con un servicio (ver abajo).

El relay crea las carpetas `relay-data/` (con `codes.json`) y `logs/`. Son relativas a la **carpeta desde la que se arranca**, así que arráncalo siempre desde la misma (el servicio de abajo lo hace con `WorkingDirectory`).

## Servicio systemd

Para que arranque con la máquina y se reinicie si se cae. Crea el archivo:

```bash
sudo nano /etc/systemd/system/multikerbal-relay.service
```

con esto (cambia `ubuntu` y la ruta si tu usuario es otro):

```ini
[Unit]
Description=MultiKerbal relay
After=network-online.target
Wants=network-online.target

[Service]
User=ubuntu
WorkingDirectory=/home/ubuntu/MultiKerbal-Relay
ExecStart=/home/ubuntu/MultiKerbal-Relay/MultiKerbal.Relay
KillSignal=SIGINT
TimeoutStopSec=30
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

`KillSignal=SIGINT` hace que al pararlo se comporte como Ctrl+C: el relay se cierra ordenadamente. Actívalo:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now multikerbal-relay
```

Comandos útiles:

- `sudo systemctl status multikerbal-relay`: si está funcionando.
- `journalctl -u multikerbal-relay -f`: ver el registro en directo (Ctrl+C para salir).
- `sudo systemctl restart multikerbal-relay`: reiniciarlo (los servidores se vuelven a registrar solos y conservan su código; los jugadores que iban por el relay se desconectan y vuelven a entrar).
- `sudo systemctl stop multikerbal-relay`: pararlo.

Como servicio no hay consola, así que no puedes escribir `list` ni `ban`. Si los necesitas, arráncalo dentro de `tmux` en vez de como servicio (`tmux`, luego `./MultiKerbal.Relay`, y Ctrl+B y luego D para dejarlo en segundo plano; `tmux attach` para volver).

## Puertos

| Puerto | Protocolo | Para qué | ¿Hay que abrirlo? |
|---|---|---|---|
| **6760** | TCP | Servidores y jugadores se conectan al relay: registro, consultas, lista y sesiones. | Sí |
| **6760** | UDP | El UDP de juego que pasa por el relay, y el «hola» de cada servidor. | Sí |
| **6761** | UDP | Las comprobaciones de acceso directo **salen** de este puerto hacia los servidores. | Ver abajo |

Sobre el **6761**: las comprobaciones salen de ahí y la respuesta del servidor vuelve al mismo puerto. Un firewall con seguimiento de conexiones (el de Oracle y el `iptables` de Ubuntu, que aceptan lo «establecido») deja pasar esa respuesta sin que abras nada. Aun así, **ábrelo también** (UDP, entrada): no cuesta nada y evita que una comprobación falle si el firewall de tu proveedor no sigue las conexiones UDP, lo que daría un falso «no accesible directamente».

Si cambias los puertos, ver [Opciones](#opciones).

En Oracle Cloud hay que abrirlos en **dos sitios**, y los dos hacen falta:

1. **En el panel de Oracle**: *Networking → Virtual cloud networks →* tu red *→ Security Lists →* la lista por defecto *→ Add Ingress Rules*. Una regla para cada puerto de la tabla: origen `0.0.0.0/0`, protocolo TCP o UDP según corresponda y el puerto de destino (`6760` TCP, `6760` UDP, `6761` UDP).
2. **Dentro de la máquina.** Las imágenes de Ubuntu de Oracle traen reglas de `iptables` que bloquean todo menos SSH, aunque el panel diga que sí. Ábrelos:
   ```bash
   sudo iptables -I INPUT 6 -m state --state NEW -p tcp --dport 6760 -j ACCEPT
   sudo iptables -I INPUT 6 -m state --state NEW -p udp --dport 6760 -j ACCEPT
   sudo iptables -I INPUT 6 -m state --state NEW -p udp --dport 6761 -j ACCEPT
   sudo netfilter-persistent save
   ```
   El `6` es la posición dentro de la cadena; tiene que quedar **antes** de la regla `REJECT` del final. Mira la lista con `sudo iptables -L INPUT --line-numbers` y ajusta el número si hace falta. `netfilter-persistent save` (viene en las imágenes de Oracle; si no, `sudo apt install iptables-persistent`) lo guarda para que sobreviva a un reinicio.

En otros proveedores, mira su *Firewall* o *Security group*, y `ufw allow 6760/tcp`, `ufw allow 6760/udp` y `ufw allow 6761/udp` si la máquina usa `ufw`.

## Comandos de consola

Se escriben en la consola del relay (la de `./MultiKerbal.Relay`, o la de `tmux`):

| Comando | Qué hace |
|---|---|
| `help` | Muestra los comandos. |
| `list` | Servidores vivos: código, nombre, jugadores, dirección, si son públicos o privados y cuánto llevan registrados. |
| `ban <código>` | Lo saca de la lista, lo desconecta y bloquea su clave: no puede volver a registrarse. El código sigue siendo suyo. |
| `unban <código>` | Lo permite otra vez. |
| `stop` | Cierra el relay (también Ctrl+C o parar el servicio). |

Los códigos se escriben como quieras: `K7M-Q2P`, `k7m q2p`, `k7mq2p`.

## Opciones

```
MultiKerbal.Relay [--port 6760] [--probe-port 6761] [--data relay-data]
```

- `--port`: puerto TCP y UDP del relay (por defecto `6760`).
- `--probe-port`: puerto UDP desde el que salen las comprobaciones (por defecto, `6761`).
- `--data`: dónde se guardan los datos (por defecto, `relay-data`).

**Haz copia de `relay-data/codes.json`.** Ahí está qué código tiene cada servidor. Si se pierde, los servidores recibirían códigos nuevos y los jugadores tendrían que enterarse. Si el archivo se corrompe, el relay **se niega a arrancar** para no repartir códigos que ya existen: repáralo o restáuralo desde la copia. El archivo guarda solo una huella de cada clave, nunca la clave misma.

## Límites

Contra abusos, el relay admite como mucho: **5 servidores por IP**, **64 sesiones por servidor**, **1 MB/s por sesión** en cada sentido, mensajes de **64 KB** y una lista pública de **500** servidores (nombres de hasta 40 caracteres). Una conexión que no manda un primer mensaje válido en 10 segundos se cierra.

## Apuntar el servidor y el mod a otro relay

Por defecto, los dos usan `multikerbal.duckdns.org:6760`. Para usar el tuyo:

**El servidor**, en `server.json`:

```json
"Relay": true,
"RelayAddress": "mi-relay.duckdns.org:6760",
"Public": false
```

(`RelayAddress` es `nombre` o `nombre:puerto`; sin puerto, `6760`. Reinicia el servidor tras cambiarlo. Con `"Relay": false` el servidor no se registra en ningún relay.)

**Cada jugador**, en el archivo `settings.cfg` del mod (`<carpeta de KSP>/PluginData/MultiKerbal/settings.cfg`), con KSP cerrado: dentro del bloque `MULTIKERBAL`, una línea

```
relayAddress = mi-relay.duckdns.org:6760
```

Los servidores y los jugadores de relays distintos no se ven: cada relay tiene sus propios códigos y su propia lista.

## Si algo falla

- **El servidor dice «Conectando con ...» y no pasa de ahí:** no llega al relay. Prueba desde otro equipo y revisa los dos sitios donde se abren los puertos (panel y `iptables`).
- **Los servidores aparecen siempre como «por el relay» aunque estén bien abiertos:** la comprobación de acceso directo no ha pasado. Revisa que el `6761/udp` esté abierto y que el servidor tenga abierto su puerto de juego (TCP **y** UDP).
- **El relay no arranca y habla de `codes.json`:** el archivo está dañado. Restáuralo desde la copia.
- **Los códigos han cambiado tras reinstalar:** se borró `relay-data/`. Restaura la copia de `codes.json`.
