# eq2advanced ACT plugin

An [Advanced Combat Tracker](https://advancedcombattracker.com/) plugin for
**EverQuest II** that sends the combat log ACT is already reading to
[eq2advanced.com](https://eq2advanced.com).

## Log Filtering

Filtering happens in the plugin. See `EQ2Advanced/Ingest/ChatFilter.cs`. Full policy:
<https://eq2advanced.com/privacy>.

**Uploaded automatically, cannot be disabled:**
- Combat log — damage, heals, deaths, cures, zoning
- NPC dialogue

**Always filtered out:**
- Tells
- Guild chat
- Officer chat
- Local `/say`
- Any channel this plugin doesn't recognize

**Uploaded optionally, off by default — two independent checkboxes:**
- Group and raid chat during a fight (±90s) — e.g. loot tracking
- General/LFG/Auction — feeds eq2advanced.com's public chat page

## Install and pair

1. In ACT: **Plugins → Plugin Listing → Browse**, pick `EQ2Advanced.dll`,
   **Add/Enable**. An **eq2advanced** tab appears.
2. On eq2advanced.com, open **Import → Plugin setup** and copy the API key.
3. Paste the key into the plugin's **This device** box and hit **Pair**. One key
   covers every character you play; the plugin reads the name off the log file.
4. Tick **Send my combat log to eq2advanced as I play**.

Settings persist to `<ACT AppData>\Config\EQ2Advanced.json`.

## Importing logs you already have

**Import logs you already have → Choose log files...** takes any number of EQ2
logs — point it at a whole folder of them — and sends them up. Install the
plugin, then load in years of old raids in one go.

They are processed **one file at a time, oldest first**, with a short pause
between batches, so a big import doesn't swamp the server that is also serving
the website. Each log becomes its own night on the site. Progress runs across
the whole import, not per file, and **Stop** halts after the current batch.

## Layout

```
EQ2Advanced/
  Plugin.cs                 IActPluginV1 entry point
  Core/                     Settings + JSON persistence + pairing
  Net/ApiClient.cs          the ingest API client
  Ingest/LogTail.cs         verbatim file tailing, second-boundary batch cutting
  Ingest/Uploader.cs        the worker: live tailing + backfill
  Ui/ConfigTab.cs           the eq2advanced tab inside ACT
tools/fetch-act.sh          downloads the genuine ACT to build against
Thirdparty/ACT/             that download (not committed)
```

`Thirdparty/ACT` holds the ACT release the plugin compiles against, downloaded
by `tools/fetch-act.sh` and not committed. The plugin **must** be built against
the genuine strong-named `Advanced Combat Tracker.exe`: a stand-in facade
produces a reference with `PublicKeyToken=null`, which is a different assembly
identity that ACT's binding redirect does not cover, and loading it fails with
`HRESULT 0x80131040`. That bug shipped here once.
