---
name: channel-map
description: Build or update LinTv's channel-map.json (extra guide display-names such as mapping 13.1 WTVT-DT to FOX) for the user's TV market. Use when asked to map channels to networks, fix Jellyfin/Plex guide matching, or fill in channel-map.json.
---

# Build the channel map

Follow `docs/LLM-Channel-Map-Instructions.md` in this repo. It covers the format and the naming rules. The steps with the live server:

1. **Get the lineup:** `curl -s http://$LINTV_HOST:5249/lineup.json` (the host is also in `publish.settings.json`). Then get the existing map: `curl -s http://$LINTV_HOST:5249/channel-map`.
2. **Ask the user for their metro** if it isn't known. Call signs alone can be ambiguous.
3. **Draft the entries** for channels that aren't already mapped. Leave out anything you're unsure of, and list what you left out. Affiliations change, so suggest checking RabbitEars.info.
4. **Show the proposed JSON and get confirmation** before writing. It's the user's data, and a wrong network name misleads guide matching.
5. **Apply** one channel at a time. That replaces only those channels and keeps any hand edits to others:
   ```bash
   curl -s -X PUT -H "Content-Type: application/json" -d '["FOX"]' http://$LINTV_HOST:5249/channel-map/13.1
   ```
6. **Verify:** `curl -s http://$LINTV_HOST:5249/guide.xml` should show the new `<display-name>` elements under each `<channel id="major.minor">`.
