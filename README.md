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
  "LockWaitSeconds": 5,
  "ChannelScanTimeoutSeconds": 5,
  "EpgScanTimeoutSeconds": 60,
  "LogRetentionDays": 7,
  "FriendlyName": "LinTv",
  "DeviceId": "4C696E54"
}
```

- `Urls` binds all interfaces so the service is reachable on the LAN. The ASP.NET default is `localhost:5000`, which is loopback only.
- `StorageDirectory` holds the scanned lineup (`channels.json`), the guide (`guide.json`), your channel map (`channel-map.json`) and the log files (`logs/`).
- `Adapter` is the `N` in `/dev/dvb/adapterN`.
- `FriendlyName` and `DeviceId` identify the tuner to Plex and Jellyfin. `DeviceId` is 8 hex digits. Keep it stable, because changing it makes clients see a new device and lose its channel mapping.
- `LockWaitSeconds` is how long to wait for a signal lock before treating an RF channel as empty.
- `ChannelScanTimeoutSeconds` is how long a scan waits for the virtual channel table on an RF channel that has a signal lock.
- `LogRetentionDays` is how many days of log files to keep in `{StorageDirectory}/logs` (see **Logs**).
- `EpgScanTimeoutSeconds` is the most time a guide scan spends on each multiplex. If it runs out, the scan keeps what it has collected, which is usually the next several hours.

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

Open `http://<host>:5249/` and start a channel scan, or use `curl` (see **Usage**). Once the scan completes, `lineup.json` should list your channels. While a stream is playing, `dvb-fe-tool -a 0 -m` in another shell shows the signal lock.

## Usage

| Endpoint | Purpose |
|---|---|
| `GET /` | Test page with buttons to start scans |
| `POST /scan/channels` | Start a channel scan in the background (409 if one is running) |
| `GET /scan/channels` | Channel scan progress and result |
| `POST /scan/epg` | Start a guide scan of every multiplex in the lineup |
| `GET /scan/epg` | Guide scan progress and result |
| `GET /stream/{major}.{minor}` | Stream a virtual channel as a single-program MPEG-TS, e.g. `/stream/8.1` |
| `GET /stream/{major}.{minor}/{index}` | Stream a specific place the channel is received, e.g. `/stream/10.1/1` (index 0 = `/stream/10.1`) |
| `GET /lineup.m3u` | Scanned channels as an M3U playlist |
| `GET /guide.xml` | Guide as XMLTV |
| `GET /channel-map` | Extra guide display-names per channel |
| `PUT /channel-map/{major}.{minor}` | Set a channel's extra names, body `["FOX"]` |
| `DELETE /channel-map/{major}.{minor}` | Remove a channel's extra names |
| `GET /logs?lines=N| `GET /guide.xml` | Guide as XMLTV |file=F` | Tail of a log file as plain text (default: newest file, 200 lines) |
| `GET /logs/files` | Log files, newest first |
| `GET /discover.json` | HDHomeRun device info (`DeviceID`, `TunerCount`, `LineupURL`) |
| `GET /lineup.json` | HDHomeRun lineup (`GuideNumber`, `GuideName`, `URL`) |
| `GET /lineup_status.json` | HDHomeRun scan status (`ScanInProgress`, `Progress`, `Found`) |

The HDHomeRun endpoints (`discover.json`, `lineup*.json`) use HDHomeRun's own PascalCase keys, because Plex and Jellyfin match on the exact names. The other JSON endpoints use ASP.NET's default camelCase.

A channel scan tunes US RF channels 2–36 and reads the ATSC virtual channel table on each one that locks. It saves the result to `channels.json`. Each empty RF channel costs `LockWaitSeconds`, so a full scan takes a few minutes. If the scan finds nothing, for example because the antenna is disconnected, the existing lineup is kept.

```bash
curl -X POST http://localhost:5249/scan/channels
curl http://localhost:5249/scan/channels # poll until "inProgress": false
curl http://localhost:5249/lineup.json
```

To watch in VLC, open `http://<host>:5249/lineup.m3u` as a network stream. VLC then shows the playlist as a list of channels.

Each stream carries only the one program, not the whole RF multiplex. The server rewrites the program list (PAT) to include just this channel and passes through the channel's PMT, audio, video and timing (PCR) streams. Other subchannels, PSIP and null packets are dropped. If the program isn't in the multiplex within 5 seconds, for example because the station renumbered, the stream returns 503 and you should run a new channel scan.

The same `major.minor` can be received on several RF channels, for example from a translator or a neighbouring market. A scan keeps every copy and records each one's signal (`SignalSnrDb`, `SignalStrengthPercent`). It ranks copies by SNR, then strength, then RF, and stores the rank as `Index` 0, 1 and so on in `channels.json`. So `/stream/{major}.{minor}` (index 0) is the best connection at the time of the scan. The lineups (`lineup.json`, `lineup.m3u`, `guide.xml`) list only the primary (index 0), because clients need each channel number to be unique. Alternates are reachable at `/stream/{major}.{minor}/{index}`.

### Guide (EPG)

A guide scan reads the guide data that stations broadcast alongside their channels (ATSC PSIP):
- **MGT:** lists which PIDs carry the event tables and descriptions.
- **EIT:** each table covers 3 hours of events per channel, keyed by the channel's `SourceId`.
- **ETT:** holds the event descriptions.
- **STT:** gives the offset between GPS time and UTC.

The scan tunes each multiplex in the lineup once, because one multiplex's tables cover all of its subchannels. It stops as soon as every listed table has arrived, or after `EpgScanTimeoutSeconds`. Most stations broadcast somewhere between 12 hours and a few days of guide data.

Each scan merges into `guide.json`. For each channel, the new events replace any stored events in the time window they cover, so rescheduled or cancelled programmes disappear. Programmes that ended more than 6 hours ago are dropped.

```bash
curl -X POST http://localhost:5249/scan/epg
curl http://localhost:5249/scan/epg      # poll until "inProgress": false
curl http://localhost:5249/guide.xml
```

XMLTV channel ids are the `major.minor` numbers, matching `GuideNumber` in `lineup.json` and `tvg-id` in the M3U. In Jellyfin, add `http://<host>:5249/guide.xml` under Live TV → TV Guide Data Providers → XMLTV. In Plex, choose the XMLTV guide option during DVR setup and give it the same URL.

Limitation: titles or descriptions sent with ATSC Huffman compression (A/65 Annex C) are skipped for now. That's rare for US broadcast TV.

### Channel map

A channel map adds extra `display-name`s to a channel in `guide.xml`. For example, 13.1 broadcasts as `WTVT-DT`, and mapping it to `FOX` lets Jellyfin or Plex match the channel to its network's guide listing. Set a mapping over the API:

```bash
curl -X PUT -H "Content-Type: application/json" -d '["FOX"]' http://localhost:5249/channel-map/13.1
```

Or edit `{StorageDirectory}/channel-map.json` directly. It accepts comments, trailing commas and any property-name casing. Changes are picked up on the next request, with no restart needed.

```json
[
  { "Channel": "13.1", "DisplayNames": [ "FOX" ] },
  { "Channel": "8.1",  "DisplayNames": [ "NBC" ] }
]
```

### Logs

Besides the console (journald under systemd), logs are written to daily files, `{StorageDirectory}/logs/lintv-YYYYMMDD.log`. Files older than `LogRetentionDays` are deleted. Read them over HTTP:

```bash
curl "http://localhost:5249/logs?lines=500"
curl "http://localhost:5249/logs?lines=500" | grep -E "WRN|ERR"
```

Levels come from the normal `Logging` section. `"LinTv": "Trace"` shows tuner and stream internals. To give the file its own levels, which is useful to keep the console quieter under systemd, use `Logging:File`:

```json
"Logging": {
  "LogLevel": { "Default": "Information", "Microsoft.AspNetCore": "Warning", "LinTv": "Information" },
  "File":     { "LogLevel": { "LinTv": "Trace" } }
}
```

Log lines worth knowing when the tuner misbehaves:

| Message | Meaning |
|---|---|
| `No data from dvr0 for Ns -- tuner stalled or signal lost?` | A stream is open but no packets are arriving. The message includes the current lock and SNR readings. |
| `Waited Ns for tuner gate` | Something held the tuner lock for a long time, such as a slow tune or a stuck caller. |
| `Tuner released with no holders` | A release without a matching acquire (a bug). The holder count is reset to 0. |
| `No lock after N ms (strength, SNR)` | Tuning failed. Strength and SNR show whether there was any signal at all. |
| `Stream X for client <outcome> after Ns (MB, Mbps)` | One line per stream when it ends: client disconnected, program not found, or failed. |
