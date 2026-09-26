---
name: channel-map
description: Build or update LinTv's channel-map.json (network names such as mapping 13.1 WTVT-DT to FOX; the first name becomes the channel name in lineup.json and all are added as XMLTV display-names) for the user's TV market. Use when asked to map channels to networks, fix Jellyfin/Plex guide matching, or fill in channel-map.json.
---

# Build the channel map

Follow `docs/LLM-Channel-Map-Instructions.md` in this repo. It covers the format and the naming rules. The steps with the live server:

1. **Get the channels:** read `/var/lib/lintv/channels.json`, the scan result. With SSH key auth, use `ssh $LINTV_USER@$LINTV_HOST cat /var/lib/lintv/channels.json`. The host and user are also in `publish.settings.json`. Otherwise ask the user to paste it. Don't start from `lineup.json`: its `GuideName` already has the current map merged in. Then get the existing map: `curl -s http://$LINTV_HOST:5249/channel-map`.
2. **Ask the user for their metro** if it isn't known. Call signs alone can be ambiguous.
3. **Draft the entries** for channels that aren't already mapped. Leave out anything you're unsure of, and list what you left out. Affiliations change, so suggest checking RabbitEars.info.
4. **Show the proposed JSON and get confirmation** before writing. It's the user's data, and a wrong network name misleads guide matching.
5. **Apply** one channel at a time. That replaces only those channels and keeps any hand edits to others:
   ```bash
   curl -s -X PUT -H "Content-Type: application/json" -d '["FOX"]' http://$LINTV_HOST:5249/channel-map/13.1
   ```
6. **Verify:** `curl -s http://$LINTV_HOST:5249/lineup.json` should show the first mapped name as each channel's `GuideName`. That's what Jellyfin's HDHomeRun tuner uses, so order matters. `guide.xml` should show all mapped names as extra `<display-name>` elements.
