# LinTv

**Turn a Linux PC with an ATSC tuner card into a network TV tuner that Plex, Jellyfin and VLC can use.**

LinTv presents itself as an [HDHomeRun](https://www.silicondust.com/), the network tuner that media servers already support. It's built for a Hauppauge WinTV-HVR-1800 PCIe card receiving US/Canadian over-the-air (ATSC 1.0) broadcasts. It should work with any ATSC card that has a Linux DVB driver.

## What it does

- **Channel scan:** finds every station your antenna can receive, including subchannels (8.1, 8.2, …). If a channel comes in on more than one frequency, it picks the strongest signal.
- **Live TV:** streams each channel over HTTP as a standard MPEG-TS, containing just that channel.
- **Program guide:** reads the guide data stations broadcast over the air and serves it as XMLTV, with no internet guide service needed.
- **HDHomeRun emulation:** serves `discover.json`, `lineup.json` and `lineup_status.json`, so Plex and Jellyfin can add it as a tuner.
- **Channel map:** attach network names (e.g. `FOX`) to local call signs to make guide matching easier.
- **Diagnostics:** keeps daily log files you can read over HTTP.

For how it works inside (tuner sharing, PSIP parsing, demuxing), see [docs/ABOUT.md](docs/ABOUT.md).

## Requirements

- A Linux box (Debian/Ubuntu below) with an ATSC tuner card that shows up under `/dev/dvb`
- An antenna
- The .NET 10 ASP.NET Core runtime
- To build and publish from a dev machine: the .NET 10 SDK and PowerShell (`publish.ps1`)

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

### 3. One-time server setup

Create a service user and the install and state directories:

```bash
sudo useradd --system --no-create-home --groups video lintv
sudo mkdir -p /opt/lintv
sudo chown -R $USER:lintv /opt/lintv
sudo find /opt/lintv -type d -exec chmod 2750 {} +    # setgid: new files inherit the lintv group
sudo find /opt/lintv -type f -exec chmod g+r,o-rwx {} +

# State (channels, guide, channel map, logs): writable by both you and the service
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

### 4. Publish from your dev machine

`publish.ps1` (Windows PowerShell) builds for `linux-x64` and uploads the output. It looks for connection settings in this order: script parameters, then environment variables, then a gitignored `publish.settings.json` (copy it from `publish.settings.example.json`):

| Setting | Env var | Parameter |
|---|---|---|
| Host | `LINTV_HOST` | `-HostName` |
| User | `LINTV_USER` | `-User` |
| Password | `LINTV_PASSWORD` | `-Password` |
| Port (default 22) | `LINTV_PORT` | `-Port` |

```powershell
.\publish.ps1 -RemoteDir /opt/lintv    # deploy straight to the install dir
.\publish.ps1                          # or stage to /tmp/lintv
```

Each run empties the target directory before extracting, so stale files from earlier builds don't linger. That includes `appsettings.json`, so keep server-specific settings somewhere the deploy won't overwrite them (see **Configure**).

With a password set, the upload goes through the [Posh-SSH](https://github.com/darkoperator/Posh-SSH) module, because Windows OpenSSH can't take a password non-interactively. Install it once with `Install-Module Posh-SSH -Scope CurrentUser`. With no password set, the script uses plain `ssh`/`scp` with SSH key auth.

### 5. Configure

Settings live in the `LinTv` section of `/opt/lintv/appsettings.json`:

```json
"Urls": "http://0.0.0.0:5249",
"LinTv": {
  "StorageDirectory": "/var/lib/lintv",
  "Adapter": 0,
  "LockWaitSeconds": 5,
  "ChannelScanTimeoutSeconds": 5,
  "EpgScanTimeoutSeconds": 60,
  "LogRetentionDays": 7,
  "FriendlyName": "LinTv",
  "DeviceId": "4C696E54"
}
```

| Setting | Meaning |
|---|---|
| `Urls` | Listen address. `0.0.0.0` makes it reachable on the LAN. The ASP.NET default, `localhost:5000`, is loopback only. |
| `StorageDirectory` | Holds the lineup (`channels.json`), the guide (`guide.json`), your channel map (`channel-map.json`) and the logs (`logs/`). |
| `Adapter` | The `N` in `/dev/dvb/adapterN`. |
| `LockWaitSeconds` | How long to wait for a signal lock before treating an RF channel as empty. |
| `ChannelScanTimeoutSeconds` | How long a scan waits for a locked channel's channel table. |
| `EpgScanTimeoutSeconds` | The most time a guide scan spends per frequency. If it runs out, it keeps what it has collected, usually the next several hours. |
| `LogRetentionDays` | Days of log files to keep. |
| `FriendlyName` | The name Plex and Jellyfin show. |
| `DeviceId` | 8 hex digits identifying the tuner to clients. **Keep it stable**: changing it makes clients see a new device and lose their channel setup. |

Any setting can also be set with an environment variable, which survives redeploys. For example, `LinTv__Adapter=1` in the systemd unit.

### 6. Run as a systemd service

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

## Getting started

1. **Scan for channels.** Open `http://<host>:5249/` and click **Start channel scan**. A full scan takes a few minutes. Watch progress at `/scan/channels`, or with `curl http://<host>:5249/scan/channels`.
2. **Scan the guide.** On the same page, click **Start EPG scan**. It takes up to `EpgScanTimeoutSeconds` per frequency.
3. **Watch something.** Open `http://<host>:5249/lineup.m3u` in VLC (Media → Open Network Stream) to get a channel list.
4. **Add it to your media server:**
   - **Jellyfin:** Dashboard → Live TV → Tuner Devices → Add → HDHomeRun, `http://<host>:5249`. Then go to TV Guide Data Providers → Add → XMLTV, `http://<host>:5249/guide.xml`.
   - **Plex:** Settings → Live TV & DVR → Set up → "Don't see your device?", `<host>:5249`. When asked for a guide, choose XMLTV, `http://<host>:5249/guide.xml`.

   Clients won't find LinTv on their own yet, so enter the address by hand.

Rerun the channel scan if stations change frequency or number. A stream that returns 503 with "not found … try a channel scan" is the sign. Rerun the EPG scan to refresh the guide.

## Channel map

Stations broadcast their call sign (`WTVT-DT`), but you may want the network (`FOX`) in the guide, for example to match channels in Jellyfin. A channel map adds extra names to the guide:

```bash
curl -X PUT -H "Content-Type: application/json" -d '["FOX"]' http://<host>:5249/channel-map/13.1
```

Or edit `/var/lib/lintv/channel-map.json` directly. Changes apply on the next request, with no restart needed:

```json
[
  { "Channel": "13.1", "DisplayNames": [ "FOX" ] },
  { "Channel": "8.1",  "DisplayNames": [ "NBC" ] }
]
```

To have an AI assistant draft the map for your area, give it [docs/LLM-Channel-Map-Instructions.md](docs/LLM-Channel-Map-Instructions.md) along with your `lineup.json`.

## Endpoints

| Endpoint | Purpose |
|---|---|
| `GET /` | Test page: start scans, links to everything below |
| `POST /scan/channels`, `GET /scan/channels` | Start a channel scan in the background (409 if one is running) / show its progress |
| `POST /scan/epg`, `GET /scan/epg` | Start a guide scan / show its progress |
| `GET /stream/{major}.{minor}` | Live stream of a channel, e.g. `/stream/8.1` (the best-signal copy) |
| `GET /stream/{major}.{minor}/{index}` | A specific copy of a channel received on several frequencies, e.g. `/stream/10.1/1` |
| `GET /lineup.m3u` | Channels as an M3U playlist |
| `GET /guide.xml` | Program guide as XMLTV |
| `GET /discover.json`, `/lineup.json`, `/lineup_status.json` | HDHomeRun emulation for Plex/Jellyfin |
| `GET /channel-map`, `PUT`/`DELETE /channel-map/{major}.{minor}` | View / set / remove extra guide names |
| `GET /logs?lines=N&file=F`, `GET /logs/files` | Read the log (default: newest file, last 200 lines) / list log files |

## Troubleshooting

**Logs.** Logs go to the console (journald) and to daily files in `/var/lib/lintv/logs`:

```bash
curl "http://<host>:5249/logs?lines=500"
curl "http://<host>:5249/logs?lines=500" | grep -E "WRN|ERR"
```

For tuner and stream detail, set `"LinTv": "Trace"` under `Logging:LogLevel`. To send the detail only to the file and keep journald quieter, set it under `Logging:File:LogLevel`:

```json
"Logging": {
  "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning", "LinTv": "Information" },
  "File":     { "LogLevel": { "LinTv": "Trace" } }
}
```

Messages worth knowing:

| Message | Meaning |
|---|---|
| `No data from dvr0 for Ns -- tuner stalled or signal lost?` | A stream is open but no packets are arriving. Includes the current lock and SNR. |
| `Waited Ns for tuner gate` | Something held the tuner for a long time, such as a slow tune or a stuck caller. |
| `No lock after N ms (strength, SNR)` | Tuning failed. Strength and SNR show whether there was any signal at all. |
| `Tuner released with no holders` | A bug: a release without a matching acquire. |
| `Stream X for client <outcome> after Ns (MB, Mbps)` | One line per stream when it ends. |

**Common problems:**

- **`Permission denied` on `frontend0`:** the user isn't in the `video` group, or hasn't logged in again since being added.
- **Listening on `localhost:5000`:** `appsettings.json` wasn't found or has no `Urls` setting.
- **"Tuner in use":** there's one tuner. A stream on one frequency blocks scans and streams on other frequencies until it ends.
- **Missing guide text for a station:** it may send compressed text, which isn't supported yet (see [ABOUT](docs/ABOUT.md#known-limitations--next-steps)).

## Development

The solution is `LinTv.slnx`: .NET 10, with package versions in `Directory.Packages.props`. It builds and runs on Windows, but tuning needs Linux. See [CLAUDE.md](CLAUDE.md) for conventions and [docs/ABOUT.md](docs/ABOUT.md) for the architecture.

## License

MIT, see [LICENSE.md](LICENSE.md).
