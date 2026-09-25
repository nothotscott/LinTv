# How LinTv works

This is the technical companion to the [README](../README.md). It covers how the pieces fit together, what the broadcast data looks like, and the design decisions (and their reasons) that aren't obvious from the code.

## The big picture

```
            antenna
               │  8VSB RF
     ┌─────────▼──────────┐
     │ HVR-1800 (cx23885) │   /dev/dvb/adapterN/{frontend0, demux0, dvr0}
     └─────────┬──────────┘
               │  ioctl + read()
     ┌─────────▼──────────┐
     │  LinuxDvbTuner     │   LinTv.Linux: tune, lock status, raw TS of the whole multiplex
     └─────────┬──────────┘
     ┌─────────▼──────────┐
     │ TunerArbiterService│   one physical tuner, shared by priority
     └──┬──────┬───────┬──┘
        │      │       │
  ChannelScanner  EpgScanner  StreamController
   (VCT → lineup)  (EIT/ETT → guide)  (ProgramDemuxer → one channel)
        │      │       │
   channels.json  guide.json   HTTP MPEG-TS
        └──────┴───────┴─── LinTv.Api: HDHomeRun JSON, M3U, XMLTV ──► Plex / Jellyfin / VLC
```

| Project | Contents |
|---|---|
| `LinTv.Core` | Everything platform-independent: domain records, the tuner arbiter, scanners, JSON stores, M3U/XMLTV writers, the file log sink, and the MPEG-TS/PSIP parsers (`Mpeg/`). |
| `LinTv.Linux` | `LinuxDvbTuner`, the only code that talks to the kernel. It uses P/Invoke `open`/`ioctl`/`close` against `libc`. |
| `LinTv.Api` | ASP.NET Core host. It holds controllers only; all logic lives in Core. |

Core has no Linux dependency, so everything except actual tuning can be built and exercised on Windows (see [Testing without the card](#testing-without-the-card)).

## Broadcast basics

An ATSC broadcast is received on an **RF channel** (a 6 MHz slot, e.g. RF 9 = 189 MHz center). Each RF channel carries one **multiplex**: an MPEG transport stream (TS) of 188-byte packets at about 19.4 Mbps. Each packet has a 13-bit **PID** saying which stream it belongs to.

A multiplex carries several **virtual channels** (e.g. 8.1 and 8.2), which are MPEG *programs*. The **PAT** (PID 0) lists programs and the PID of each one's **PMT**. The PMT lists that program's video, audio and PCR (clock) PIDs.

ATSC adds **PSIP** tables on base PID `0x1FFB`:

| Table | ID | Used for |
|---|---|---|
| VCT (TVCT/CVCT) | `0xC8`/`0xC9` | major.minor number, short name, program number, `source_id` per channel → channel scan |
| MGT | `0xC7` | Which PIDs carry the EITs and ETTs → EPG scan |
| EIT-k | `0xCB` | Events for a 3-hour window k, keyed by `source_id` → titles and times |
| ETT | `0xCC` | Extended text (descriptions), linked to an event by ETM_id |
| STT | `0xCD` | GPS-to-UTC offset (leap seconds). EIT times are GPS seconds since 1980-01-06. |

All these tables arrive as **sections**, which may span packets. `PsiSectionAssembler` reassembles them and checks their CRC.

## The tuner (`LinTv.Linux`)

- **Tuning:** a single `FE_SET_PROPERTY` ioctl sends `DTV_CLEAR`, delivery system ATSC, 8VSB, frequency, auto inversion and `DTV_TUNE`. The ioctl only queues the tune. `WaitForLockAsync` then polls `FE_READ_STATUS` for `FE_HAS_LOCK`.
- **Signal:** strength and SNR come from the legacy `FE_READ_SIGNAL_STRENGTH`/`FE_READ_SNR` ioctls. Their units are driver-specific: the HVR-1800's s5h1409 demod gives strength 0–65535 and SNR in tenths of a dB.
- **The frontend fd stays open for the process lifetime.** Closing it lets the driver power the tuner down and drop the tune.
- **Reading:** a pass-through demux filter (`DMX_SET_PES_FILTER`, PID `0x2000` = all, output `DMX_OUT_TS_TAP`) makes `dvr0` a plain byte stream of the whole multiplex. The filter lives as long as the demux fd, so it's opened per read and closed in `finally`. Only one reader can have `dvr0` open.
- **Struct layouts** (`DtvProperty` 76 bytes, ioctl numbers) assume a 64-bit process, and the tuner throws otherwise.
- **Stall watchdog:** a `dvr0` read blocks rather than failing when data stops, so a timer logs a warning every 5 s with no data.

## Sharing the tuner (`TunerArbiterService`)

There is one physical tuner, so the arbiter decides who gets it:

- **Acquire:** `AcquireAsync(frequency, priority)` tunes and waits for lock if the tuner is idle. If the tuner is already on that frequency, the caller **shares** it; holders are counted. On a different frequency the caller gets `TunerBusyException`, unless it has higher priority, in which case preemption would apply (not implemented yet).
- **Priorities:** `BackgroundEpg` < `ChannelScan` < `LiveView`.
- **Release:** every acquire must be paired with `ReleaseAsync(CancellationToken.None)` in a `finally`. The token is `None` because the request token is typically already cancelled when a stream ends, and a skipped release leaves the tuner "busy" forever.

## Channel scan (`ChannelScanner`)

1. **Tune:** for each RF in `AtscChannelPlan.UsBroadcast` (US/Canada RF 2–36, post-repack), acquire the tuner. Dead channels cost `LockWaitSeconds`.
2. **Read the VCT:** read the multiplex until the VCT is complete (all sections of the current version), or `ChannelScanTimeoutSeconds`.
3. **Filter:** keep entries that are visible, 8VSB, digital TV or audio, and carried in this multiplex (`channel_TSID` matches).
4. **Sample the signal:** take the signal twice (at lock and after the VCT) and average.
5. **Rank:** at the end, group by `major.minor` and rank by SNR, then strength, then RF. The rank becomes `VirtualChannel.Index`.
6. **Save:** replace `channels.json`, unless nothing was found (e.g. antenna unplugged).

### One channel, many frequencies

The same `major.minor` can be received on several RFs, such as a translator or a neighbouring market. All copies are stored. **Index 0 (the "primary") is the best signal at scan time.**

- `/stream/10.1` plays the primary, and `/stream/10.1/1` plays the next best copy.
- `lineup.json`, the M3U and `guide.xml` list **primaries only**, because clients key channels on `GuideNumber` / `tvg-id` / the XMLTV channel id and those must be unique.
- The EPG scan also reads primaries only; alternates carry the same programmes.

## Streaming one channel (`ProgramDemuxer`)

`/stream/{major}.{minor}/{index?}` acquires the tuner at `LiveView` priority and runs the multiplex through `ProgramDemuxer`:

- **PAT:** the demuxer follows the PAT to the program's PMT PID. It then emits a **rewritten PAT** that lists only this program each time the source PAT repeats (~100 ms). The rewritten PAT keeps the source TSID and version and has its own continuity counter.
- **Pass-through:** the PMT and the elementary-stream and PCR PIDs it lists pass through unmodified. Everything else is dropped: other programs, PSIP and null packets.
- **Station changes:** if the PMT moves or changes, output switches to the new streams.
- **Missing program:** if the program isn't in the PAT within 5 s (the station renumbered), the endpoint returns 503. It's still possible because nothing has been written yet.

**Gotcha:** set response headers (`Content-Type`) *before* the first body write. After that the response has started and headers are read-only.

## Guide scan (`EpgScanner`, `PsipGuideCollector`)

- **Collecting:** the scanner tunes each multiplex that has a primary channel, at `BackgroundEpg` priority. It skips a multiplex with a warning if the tuner is busy elsewhere, and shares the tuner if a viewer is on the same one. `PsipGuideCollector` starts on `0x1FFB`, learns the EIT/ETT PIDs from the MGT, and follows them.
- **Done when:** every EIT listed in the MGT is complete for every wanted `source_id`, and every event that points at an ETT has its text. Or `EpgScanTimeoutSeconds` runs out, in which case the partial data is kept; the near-term EITs arrive first.
- **Text:** titles and descriptions are ATSC *multiple string structures*. English is preferred. Huffman-compressed strings (A/65 Annex C) aren't decoded yet and are skipped.
- **Merging into `guide.json`:** per channel, new events replace stored events that start inside the window the new events cover. This handles reschedules and cancellations. Events that ended more than 6 h ago are pruned.

## Client-facing formats

| Endpoint | Format notes |
|---|---|
| `discover.json`, `lineup.json`, `lineup_status.json` | HDHomeRun's exact **PascalCase** keys (`HdHomeRunJson.Options`), because clients match names exactly. `discover.json` claims model `HDHR5-2US` with `TunerCount` 1. `DeviceID` must stay stable, because clients key the device on it. |
| `lineup.m3u` | Extended M3U with `tvg-id` = `major.minor`, and `x-tvg-url` pointing at `guide.xml`. |
| `guide.xml` | XMLTV. Channel id = `major.minor`. Display names are `"13.1 WTVT-DT"`, `"WTVT-DT"`, `"13.1"`, then any channel-map names (e.g. `"FOX"`). |

Stream and guide URLs are built in one place, `ChannelUrls`, which must match the controller routes. `BaseURL` is taken from the request's `Host`, so it's right whichever name or IP the client used.

## Storage

Everything lives in `StorageDirectory` (default `/var/lib/lintv`):

| File | Written by | Notes |
|---|---|---|
| `channels.json` | Channel scan | `VirtualChannel[]` including `Index` and signal readings |
| `guide.json` | EPG scan (merge) | `GuideEvent[]` |
| `channel-map.json` | You (by hand or `PUT /channel-map`) | Reloaded when its timestamp changes. Comments, trailing commas and any casing are allowed. |
| `logs/lintv-YYYYMMDD.log` | `FileLoggerProvider` | Daily files, `LogRetentionDays` retention |

The stores cache in memory and write via temp file + rename (`JsonFile.WriteAtomicAsync`), so a crash never leaves a truncated file. Old `channels.json` files without `Index` or signal fields still load, and the missing fields default to 0.

## Logging

`FileLoggerProvider` (alias `File`) is registered as an `ILoggerProvider` in DI:

- **Background writes:** a bounded queue drains on a background task that drops the oldest lines if the disk stalls, so logging never blocks streaming.
- **Levels:** they follow `Logging:LogLevel`, and `Logging:File:LogLevel` overrides them for the file only.
- **Reading:** `/logs` tails a file by reading backwards, so large trace files are fine.

The logging is aimed at "is the tuner stuck?". The README lists the key messages.

## Testing without the card

There's no test project yet. The MPEG/PSIP code has been verified with throwaway **.NET 10 file-based apps** that build synthetic sections (with real CRCs), packetize them, and feed them through the framer, assembler and parsers. See `.claude/skills/offline-ts-test`. On Windows, the API runs fine for everything except tuning (`libc` isn't found). To point it at a scratch state directory, set `LinTv__StorageDirectory`.

## Known limitations / next steps

- **Guide refresh:** the EPG only updates on demand. Scheduled refresh is the next step.
- **No preemption:** a live stream can't take the tuner from a scan.
- **Huffman text:** Huffman-compressed PSIP text is skipped.
- **Stuck reads:** if a tuner stops delivering data mid-stream, the blocking `dvr0` read can't be cancelled until data arrives. The watchdog makes this visible, but it doesn't recover it.
- **Discovery:** there's no UDP discovery (port 65001), so clients need the address typed in.
