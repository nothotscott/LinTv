---
name: deploy
description: Build LinTv for linux-x64 and publish it to the tuner server with publish.ps1. Use when asked to deploy, publish, push, or "put it on the server".
---

# Deploy LinTv

1. **Build first** so compile errors don't turn up halfway through a publish:
   ```powershell
   dotnet build LinTv.slnx -nologo -v q
   ```
2. **Check for connection settings.** Use `LINTV_HOST`, `LINTV_USER` and `LINTV_PASSWORD` (optional: `LINTV_PORT`), or `publish.settings.json` in the repo root, which is gitignored. If neither is set, ask the user. Never write a password into a tracked file or echo it.
3. **Publish** straight into the install directory:
   ```powershell
   .\publish.ps1 -RemoteDir /opt/lintv
   ```
   Password auth needs the Posh-SSH module. If the script says it's missing, ask the user to run `Install-Module Posh-SSH -Scope CurrentUser`; don't install modules yourself.
4. **Warn** that the deploy replaces `/opt/lintv/appsettings.json`. Server-only settings belong in environment variables in the systemd unit (e.g. `LinTv__Adapter=1`).
5. **Restarting needs sudo,** which you don't have. Ask the user to run `sudo systemctl restart lintv`. Once they have, verify:
   ```bash
   curl -s http://$LINTV_HOST:5249/discover.json
   curl -s "http://$LINTV_HOST:5249/logs?lines=30"
   ```
   Check the startup lines for errors, and check that `Now listening on: http://0.0.0.0:5249` appears.

Report what was deployed (the commit, or "uncommitted changes") and whether the verification passed.
