# CLAUDE.md

LinTv is an HDHomeRun-compatible network tuner. It runs as an ASP.NET Core service on Linux and drives an ATSC tuner card (Hauppauge WinTV-HVR-1800) through the Linux DVB API. It serves live channels, an M3U, an XMLTV guide and HDHomeRun JSON to Plex, Jellyfin and VLC.

- User-facing docs and the deploy guide are in `README.md`.
- Architecture and broadcast/MPEG background are in `docs/ABOUT.md`. Read it before touching `LinTv.Mpeg`, the scanners, or the arbiter.

## Layout

| Project | What goes here |
|---|---|
| `LinTv.Core` | All platform-independent logic: `Domain/` records, `Services/` (arbiter, scanners), `Stores/` (JSON persistence), `Writers/` (M3U, XMLTV, `ChannelUrls`), `Logging/` (file sink) |
| `LinTv.Mpeg` | Wire formats only, no dependencies: TS framing, PSI section assembly, PSIP tables and descriptors (A/65), `PsipGuideCollector`, `ProgramDemuxer`. Maintained by Claude Code; cite the A/65 section or table for every field. |
| `LinTv.Linux` | `LinuxDvbTuner` only: P/Invoke `open`/`ioctl`/`close` on `/dev/dvb`. `AllowUnsafeBlocks` is on. |
| `LinTv.Api` | Host, DI wiring (`Program.cs`, including options validation and hosted services), thin attribute-routed controllers, and the Razor Pages UI (`Pages/`: Dashboard, Channels, Signal). HDHomeRun DTOs and `HdHomeRunJson` live in Core (`Domain/HdHomeRunModels.cs`), and `lineup.json` is built by `Services/HdHomeRunLineupService`. |

The dev machine is Windows. The deploy target is Linux x64.

## Build, run, deploy

```powershell
dotnet build LinTv.slnx
# Local run (everything works except tuning, which throws DllNotFoundException 'libc' on Windows).
# Point storage at a scratch dir, or it creates D:\var\lib\lintv:
$env:LinTv__StorageDirectory = "$env:TEMP\lintv"; dotnet run --project LinTv.Api --no-launch-profile --urls http://127.0.0.1:5399
.\publish.ps1 -RemoteDir /opt/lintv   # build linux-x64 + upload; creds from LINTV_* env vars or publish.settings.json
```

- **No test project yet.** Parsing code is verified with throwaway file-based apps; see the `offline-ts-test` skill.
- **Real tuning only happens on the server.** Ask the user to test on hardware, or read the server's logs with the `diagnose` skill.
- **Restarting the service needs sudo**, which isn't available to agents. After a deploy, ask the user to `sudo systemctl restart lintv`.

## Conventions

- **Namespaces and comments:** block-scoped namespaces (`namespace X { ... }`). Comments are `///` lines that explain *why*, and cite the spec for wire formats (e.g. "A/65 6.5", "ISO 13818-1 2.4.4.3").
- **DI shape:** services, stores and writers each have an `I*` interface and are registered as **singletons** in `Program.cs`. Classes take dependencies in the constructor and assign them to properties declared like:
  ```csharp
  public ILogger Logger { private get; set; }
  public IChannelStore ChannelStore { private get; init; }
  ```
- **Packages:** central package management. Versions go in `Directory.Packages.props`; `<PackageReference>` has no `Version`. Only `Microsoft.*` packages so far. Prefer the BCL over adding dependencies.
- **Stores:** new JSON stores use `JsonFile.ReadAsync` / `WriteAtomicAsync` and live in `StorageDirectory`, with an in-memory cache behind a `SemaphoreSlim`.
- **Logging:** structured templates. `Information` for lifecycle events (scan and stream start/end), `Debug`/`Trace` for tuner and arbiter internals, `Warning` for "something is stuck". Don't log per TS chunk.
- **Config:** new settings go on `LinTvConfiguration` with a sensible default, plus `appsettings.json` and the README's config table.
- **UI (`Pages/`):** pages read Core services directly in their `PageModel`s, and actions are form POSTs to page handlers (antiforgery on, then redirect back). JavaScript is limited to polling status and running the signal meter via the JSON API (`/scan/...`). CSS lives in `Shared/_Layout.cshtml`. No static files, bundler or frameworks.

## Gotchas (each has bitten once)

- **Arbiter leases:** `AcquireAsync` returns a `TunerLease`. Link your reads to `lease.Revoked` (with `CreateLinkedTokenSource(ct, lease.Revoked)`), and call `ReleaseAsync(lease)` in a `finally`. The release is idempotent and ignores leases revoked by `ForceReleaseAsync`, so a stuck holder that finally unwinds can't corrupt the next session. A skipped release leaves the tuner "busy" until someone disconnects it.
- **Response headers:** set them (e.g. `Content-Type`) **before** the first `Response.Body` write. After that they're read-only and the next write throws.
- **Unique ids for clients:** `lineup.json`, `lineup.m3u`, `guide.xml` and the EPG scan use **primaries only** (`VirtualChannel.IsPrimary`, i.e. `Index == 0`). Clients need `GuideNumber` / `tvg-id` / XMLTV ids to be unique. Alternates are reachable only at `/stream/{major}.{minor}/{index}`.
- **HDHomeRun JSON:** use `HdHomeRunJson.Options` (PascalCase, nulls omitted) for anything HDHomeRun-shaped. Clients match key names exactly.
- **The channel map drives Jellyfin's naming:** the first `DisplayNames` entry becomes `GuideName` in `lineup.json`. Jellyfin's HDHomeRun tuner shows and matches channels by that name, not by XMLTV display-names, so name order in `channel-map.json` matters.
- **Scheduled work:** it goes in a `BackgroundService` registered with `AddHostedService`. Scheduled scans go through the scanner's `TryStartScan`, so they share its "already running" guard.
- **`DeviceId` must never change by default:** clients key the device, and the user's channel setup, on it.
- **Keep URLs in sync:** stream and guide URLs come from `ChannelUrls`. Change it together with the `StreamController` / `GuideController` routes.
- **Content root:** it's `AppContext.BaseDirectory` (in `Program.cs`), so `appsettings.json` is found no matter where `dotnet` is started. Don't remove this.
- **Data Protection keys:** they go to `StorageDirectory/keys` (`Program.cs`). Without that, ASP.NET writes them to `$HOME/.aspnet/DataProtection-Keys`, which for the `lintv` user landed inside `/opt/lintv`. The files were owned by `lintv`, so `publish.ps1` (running as the deploy user) couldn't empty the directory. Nothing the service writes at runtime may go under `/opt/lintv`.
- **Case matters on the server:** Linux is case-sensitive and the names are `LinTv.*`, not `LinTV.*`. The repo folder itself is `LinTV`.
- **Struct layouts:** `LinuxDvbTuner` layouts (`DtvProperty` = 76 bytes, `DmxPesFilterParams`) and ioctl numbers are 64-bit-specific. Recheck them against `linux/dvb/frontend.h` / `dmx.h` before changing.
- **Tuner lifetime:** the frontend fd stays open for the process lifetime (closing it drops the tune).
- **Read the multiplex only through `IProgramStreamBroadcaster`:** `StreamProgramAsync(program)` for a demuxed channel, `ReadMultiplexAsync()` for raw PSIP (scanners). The device allows one `dvr0` reader, and the broadcaster's single pump is what lets several streams and scans share a tune. Calling `IDvbTuner.ReadTransportStreamAsync` anywhere else fails with `EBUSY` as soon as anything else is reading. `IDvbTuner` stays device-only.
- **`dvr0` is read non-blocking with `poll()`:** 500 ms slices, yielding **empty chunks** as idle ticks. Consumers must tolerate empty chunks, and use them to check deadlines. Never go back to a blocking `read()`/`FileStream`: with a dead signal it never returns, cancellation is never seen, and the tuner stays held forever (only one reader can have `dvr0` open).

## Skills

Project skills in `.claude/skills/`:
- `deploy`: publish to the server.
- `diagnose`: pull and interpret the server's logs and state.
- `offline-ts-test`: verify MPEG/PSIP code without the card.
- `channel-map`: build `channel-map.json` for the user's market.
