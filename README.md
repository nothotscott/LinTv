# LinTv

An HDHomeRun-compatible network tuner for a Hauppauge WinTV-HVR-1800 PCIe card on Linux. It is an ASP.NET Core service that uses the Linux DVB API (`/dev/dvb/adapterN`) to tune ATSC broadcasts and serves them to Plex and Jellyfin as if the card were an HDHomeRun.

Planned features:

- **Channel scan**: a scan of ATSC RF channels, published as an M3U playlist
- **EPG scan**: guide data read from the broadcast PSIP tables, published as XMLTV
- **HDHomeRun emulation**: the `discover.json`, `lineup.json` and `lineup_status.json` endpoints

## Projects

| Project | Purpose |
|---|---|
| `LinTv.Api` | ASP.NET Core host and HTTP endpoints |
| `LinTv.Core` | Domain models, configuration, tuner arbitration |
| `LinTv.Linux` | Linux DVB driver (`ioctl` against `/dev/dvb`) |

Package versions are managed centrally in `Directory.Packages.props`.

## Deploy (Debian / Ubuntu)

### 1. Verify the card

The HVR-1800 uses the in-kernel `cx23885` driver, so no extra driver install is needed.

```bash
sudo dmesg | grep -i cx23885     # driver loaded, card detected
ls /dev/dvb/adapter0             # expect: demux0  dvr0  frontend0  net0
```

If `/dev/dvb` is missing, install the firmware and reboot:

```bash
sudo apt install linux-firmware
```

The DVB tools are optional but useful for checking signal lock while LinTv holds the tuner:

```bash
sudo apt install dvb-tools
dvb-fe-tool -a 0 -m              # live frontend status / signal monitor
```

### 2. Install the .NET 10 runtime

LinTv targets `net10.0` and needs the **ASP.NET Core runtime**. Install the SDK instead if you'll build on the box.

**Ubuntu 26.04+**: .NET 10 is in the Ubuntu archive:

```bash
sudo apt update
sudo apt install aspnetcore-runtime-10.0     # or: dotnet-sdk-10.0
```

**Ubuntu 24.04**: use the Ubuntu .NET backports PPA:

```bash
sudo add-apt-repository ppa:dotnet/backports
sudo apt update
sudo apt install aspnetcore-runtime-10.0     # or: dotnet-sdk-10.0
```

**Debian**: use the Microsoft package feed (replace `12` with your Debian version):

```bash
wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
sudo dpkg -i /tmp/packages-microsoft-prod.deb
sudo apt update
sudo apt install aspnetcore-runtime-10.0     # or: dotnet-sdk-10.0
```

Verify:

```bash
dotnet --list-runtimes    # expect Microsoft.AspNetCore.App 10.0.x
```

Don't mix the Ubuntu and Microsoft feeds for the same packages. If you do, `dotnet` may fail to find installed runtimes. See the [.NET on Ubuntu docs](https://learn.microsoft.com/dotnet/core/install/linux-ubuntu) if you run into this.

### 3. Publish and copy

From the dev machine (Windows), `publish.ps1` builds for `linux-x64` and uploads the output to `/tmp/lintv`. It looks for connection settings in this order: script parameters, then environment variables, then a gitignored `publish.settings.json` (copy it from `publish.settings.example.json`):

| Setting | Env var | Parameter |
|---|---|---|
| Host | `LINTV_HOST` | `-HostName` |
| User | `LINTV_USER` | `-User` |
| Password | `LINTV_PASSWORD` | `-Password` |
| Port (default 22) | `LINTV_PORT` | `-Port` |

```powershell
.\publish.ps1                          # stage to /tmp/lintv
.\publish.ps1 -RemoteDir /opt/lintv    # deploy straight to the install dir (after the setup below)
```

Each run empties the target directory before extracting, so stale files from earlier builds don't linger.

With a password set, the upload goes through the [Posh-SSH](https://github.com/darkoperator/Posh-SSH) module, because Windows OpenSSH can't take a password non-interactively. Install it once with `Install-Module Posh-SSH -Scope CurrentUser`. With no password set, the script uses plain `ssh`/`scp` with SSH key auth.

One-time server setup:

```bash
sudo useradd --system --no-create-home --groups video lintv
sudo mkdir -p /opt/lintv
sudo chown -R $USER:lintv /opt/lintv
sudo find /opt/lintv -type d -exec chmod 2750 {} +    # setgid: new files inherit the lintv group
sudo find /opt/lintv -type f -exec chmod g+r,o-rwx {} +

# State (channels.json, guide.json): writable by both you and the service
sudo install -d -o $USER -g lintv -m 2770 /var/lib/lintv
```

After this setup:

- **You own `/opt/lintv`**, so `publish.ps1 -RemoteDir /opt/lintv` deploys without `sudo`.
- **The `lintv` group can read it but not write it.** The service doesn't need write access to its own binaries.
- **The setgid bit** (the `2` in `2750`) means files you deploy later get the `lintv` group automatically.

The `lintv` user needs the `video` group to open `/dev/dvb/*`. The `video` group only controls the device files, not `/opt/lintv`.

To run LinTv as your own user (e.g. `dotnet /opt/lintv/LinTv.Api.dll`), add yourself to that group too, or it fails with `Permission denied` on `frontend0`:

```bash
ls -l /dev/dvb/adapter0          # confirm the group is "video"
sudo usermod -aG video $USER
# log out and back in (or `newgrp video` in the current shell), then check:
groups                           # should list video
```

### 4. Configure

Edit `/opt/lintv/appsettings.json`:

```json
"Urls": "http://0.0.0.0:5249",
"LinTv": {
  "StorageDirectory": "/var/lib/lintv",
  "Adapter": 0,
  "LockWaitSeconds": 5
}
```

- `Urls` binds all interfaces so the service is reachable on the LAN. The ASP.NET default is `localhost:5000`, which is loopback only.
- `StorageDirectory` holds the scanned lineup and the guide data.
- `Adapter` is the `N` in `/dev/dvb/adapterN`.
- `LockWaitSeconds` is how long to wait for a signal lock before treating an RF channel as empty.

### 5. Run as a systemd service

`/etc/systemd/system/lintv.service`:

```ini
[Unit]
Description=LinTv HDHomeRun emulator
After=network-online.target
Wants=network-online.target

[Service]
User=lintv
Group=lintv
SupplementaryGroups=video
WorkingDirectory=/opt/lintv
ExecStart=/usr/bin/dotnet /opt/lintv/LinTv.Api.dll
Environment=DOTNET_NOLOGO=1
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now lintv
journalctl -u lintv -f
```

### 6. Smoke test

Tune a channel by its center frequency in Hz. For example, RF 9 is 189 MHz. For UHF channels, use `(RF - 14) * 6 + 473` MHz.

```bash
curl "http://localhost:5249/test?frequencyHz=189000000"
dvb-fe-tool -a 0 -m    # in another shell: look for "Lock"
```

## Usage

| Endpoint | Purpose |
|---|---|
| `GET /` | Test page with buttons to start scans |
| `POST /scan/channels` | Start a channel scan in the background (409 if one is running) |
| `GET /scan/channels` | Channel scan progress and result |
| `GET /lineup.m3u` | Scanned channels as an M3U playlist |
| `GET /auto/v{major}.{minor}` | Stream a channel, e.g. `/auto/v9.1` |
| `GET /stream?frequencyHz=N` | Stream a raw RF multiplex |

A channel scan tunes US RF channels 2–36 and reads the ATSC virtual channel table on each one that locks. It saves the result to `channels.json`. Each empty RF channel costs `LockWaitSeconds`, so a full scan takes a few minutes. If the scan finds nothing, for example because the antenna is disconnected, the existing lineup is kept.

```bash
curl -X POST http://localhost:5249/scan/channels
curl http://localhost:5249/scan/channels # poll until "inProgress": false
curl http://localhost:5249/lineup.m3u
```

To watch in VLC, open `http://<host>:5249/lineup.m3u` as a network stream. VLC then shows the playlist as a list of channels. Streams are currently the whole RF multiplex. The M3U includes a VLC-only `#EXTVLCOPT:program=` line so VLC plays the right subchannel.
