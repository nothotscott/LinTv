# Instructions: build a LinTv channel map

You are helping someone set up LinTv, an over-the-air TV tuner for US/Canada (ATSC). Your job is to produce a `channel-map.json` for their local market.

## The problem

Stations identify themselves over the air by a virtual channel number and a short call sign, for example `13.1 WTVT-DT`. Media servers such as Jellyfin and Plex match channels to guide listings and logos more reliably by the **network name** (`FOX`). The channel map supplies those names:

- **The first name becomes the channel's name** in the HDHomeRun lineup, which is what Jellyfin and Plex display and match on.
- **All names are added to the XMLTV guide** as extra display-names. The call sign stays there too.

## Input

The user gives you their metro or market (e.g. "Tampa, FL"), their `channels.json` (the channel-scan result, at `/var/lib/lintv/channels.json` on the server), or both. `channels.json` is what the channels actually broadcast, and the channel map doesn't change it:

```json
[
  { "Major": 13, "Minor": 1, "ShortName": "WTVT-DT", "RfChannel": 12, "FrequencyHz": 207000000,
    "ProgramNumber": 3, "SourceId": 1, "Index": 0, "SignalStrengthPercent": 70, "SignalSnrDb": 24.5, "Id": "13.1" },
  { "Major": 13, "Minor": 2, "ShortName": "Movies!", "RfChannel": 12, "Index": 0, "Id": "13.2" }
]
```

- **`Id` is the channel number** (`Major.Minor`). **`ShortName`** is the broadcast name, which is usually the call sign on `.1` and often the diginet on subchannels.
- **`RfChannel`** is the physical channel. Use it to tell stations apart when looking one up, because RabbitEars.info lists stations by RF channel.
- **`Index` > 0** means the same channel number was also received on another RF channel (a translator or a neighbouring market). The map applies per channel number, so map each `Id` once, based on the `Index` 0 entry.

Don't use `lineup.json` as input. It already has the current channel map merged into `GuideName`, so it shows mapped names rather than what's broadcast.

If you only have the metro, list the stations you know broadcast there, and tell the user to check the list against their `channels.json`.

## Output

Return only a JSON array, no prose inside it:

```json
[
  { "Channel": "13.1", "DisplayNames": [ "FOX" ] },
  { "Channel": "8.1",  "DisplayNames": [ "NBC" ] },
  { "Channel": "8.2",  "DisplayNames": [ "Charge!" ] }
]
```

- `Channel` is the `major.minor` string, exactly as `Id` in `channels.json`.
- `DisplayNames[0]` is the name the user will see for the channel, so make it the network or brand. Add a second name only if it's a common alternative, such as `"PBS"` and `"WEDU"`.

## Rules

1. **Main channels (`.1`)** get the broadcast network: `ABC`, `CBS`, `NBC`, `FOX`, `PBS`, `The CW`, `MyNetworkTV`, `ION`, `Telemundo`, `Univision`, `UniMás`, `Estrella TV`. An independent station gets `Independent` plus its brand (e.g. `"Tampa Bay 28"`).
2. **Subchannels (`.2`, `.3`, …)** get their diginet, spelled as the network spells it: `MeTV`, `Antenna TV`, `Comet`, `Charge!`, `Grit`, `Bounce`, `Court TV`, `Start TV`, `Heroes & Icons`, `Movies!`, `Laff`, `Cozi TV`, `Decades`/`Catchy Comedy`, `Story Television`, `Quest`, `Ion Mystery`, `Dabl`, `True Crime Network`, `Rewind TV`.
3. **Skip names the station already broadcasts.** If `ShortName` is already `Charge!`, leave that channel out.
4. **Skip entries you aren't confident about.** Affiliations and diginets change often, and a wrong name is worse than none. Tell the user which channels you left out, and suggest checking [RabbitEars.info](https://www.rabbitears.info) for their market.
5. **Only include channels in the user's `channels.json`**, if they gave you one. Never invent channel numbers.
6. **Produce the whole map.** Your output replaces the user's existing `channel-map.json`. If they also give you their current map, keep its entries unless you're confident an entry is wrong, and say which ones you changed.
7. **Output must be valid JSON.** The file does accept comments and trailing commas, but plain JSON is safest.

## Applying it

Save the array as `/var/lib/lintv/channel-map.json`. LinTv picks it up on the next request, with no restart needed. Alternatively, set one channel at a time:

```bash
curl -X PUT -H "Content-Type: application/json" -d '["FOX"]' http://<host>:5249/channel-map/13.1
```

Check the result in `http://<host>:5249/lineup.json`: each mapped channel's `GuideName` is its first name. In `guide.xml`, each mapped channel gets extra `<display-name>` elements. Jellyfin may need its tuner or guide data refreshed before it shows the new names.
