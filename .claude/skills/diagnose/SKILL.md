---
name: diagnose
description: Inspect the running LinTv server - read its logs, scan status, lineup and device info over HTTP - and explain what's wrong. Use when the user reports streams not playing, scans failing, a stuck tuner, missing guide data, or asks to "check the logs".
---

# Diagnose the running server

**Host:** use `LINTV_HOST`, or `Host` from `publish.settings.json`. The port is 5249. Everything below is read-only.

**Don't start scans or streams without asking.** They take the single tuner, and the user may be watching something.

## Gather

```bash
H=http://$LINTV_HOST:5249
curl -s "$H/logs?lines=400"                          # newest log file tail (plain text)
curl -s "$H/logs/files"                              # other days: /logs?file=lintv-YYYYMMDD.log
curl -s "$H/scan/channels"; curl -s "$H/scan/epg"    # lastError, lastCompleted, inProgress
curl -s "$H/lineup.json"; curl -s "$H/discover.json"
```

Filter the log for warnings and errors first: `grep -E "\[(WRN|ERR|CRT)\]"`. Then read the lines around the first bad one.

## Interpret

| Log text | Likely cause / next step |
|---|---|
| `No data from dvr0 for Ns -- tuner stalled or signal lost?` | Stream open, no packets. `locked False` or a low SNR means reception. Locked but no data means a driver or tuner hang, so suggest a service restart and check `dmesg` for cx23885 errors. |
| `Waited Ns for tuner gate` | A long tune, or a caller holding the gate. Check whether a scan was running at the same time. |
| `Tuner busy: ...` / 503 `Tuner in use` | Expected: there's one tuner. Something else holds it on another frequency. |
| `No lock after N ms (strength X%, SNR Y dB)` | Strength near 0 means no signal on that RF, or the antenna. Some strength but low SNR means marginal reception. |
| `Program N (...) not in the PAT` | The station renumbered or moved, so the user should rerun the channel scan. |
| `Tuner released with no holders` | A code bug: an acquire/release mismatch. Find the caller in the preceding lines. |
| `locked, but no VCT within Ns` | Weak signal, or `ChannelScanTimeoutSeconds` is too short. |
| `EPG ... partial, stopped after Ns` | Normal for far-future data. If the near-term guide is missing too, raise `EpgScanTimeoutSeconds`. |
| `DllNotFoundException: libc` | It's running on Windows. Tuning only works on Linux. |

Report: what's wrong, the evidence (quote the log lines with timestamps), and the fix. Say whether the fix is in code or something the user does, such as a restart, a rescan, or the antenna.
