# DD2 Autobattler

Experimental **BepInEx 5** plugin that plays [Darkest Dungeon II](https://www.darkestdungeon.com/) combat. You still manage the map, inn, and gear.

Not an official Red Hook mod. **Not a Steam Workshop item** (Workshop is data/CSV only; this patches the live game). Game updates can break it. It will throw runs. Restart the game after every build - there is no hot reload.

## Status

Competent on road trash (playtested Plague Doctor / Grave Robber / Highwayman / Man-at-Arms). Named fights use **cited notes** only (`notes/cited-bosses.md`: wiki Strategy + game CSVs). Current notes: Dreaming General Taproot cadence, Tangle Bishop→Drummer, Shackles of Denial lock order, Harvest Child (table, not the meats), Librarian (hit him, do not finish the books).

1-ply: paper-apply preview HP/kills/Combo onto the board so a real hit beats a 0-damage Combo mark. Overlay `one_ply`; JSONL `ply`.

Not done: remaining Confession bosses, gambits. Retreat is opt-in and off. Cursed relationship skills lose to a clean alternative. A healer who cannot launch their heal will walk into rank if an ally is in crisis.

## Requirements

- Darkest Dungeon II on PC (Steam)
- [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) in the game folder (x64 / Mono)
- .NET SDK that can target `net472`

## Build

Copy `Directory.Build.props.user.example` to `Directory.Build.props.user` and set `GameDir` to your install (or set env `DD2_GAME_DIR`). That file is gitignored.

```
dotnet build src\Dd2Autobattler\Dd2Autobattler.csproj -c Release
```

A successful build copies `Dd2Autobattler.dll` into `BepInEx\plugins`. Then **restart the game**.

Config: `BepInEx\config\drednot.dd2.autobattler.cfg`

- `Combat.Enabled` - turn the bot off to play a fight yourself
- `Logging.LogPreviews` - include every scored action in the JSONL
- `UI.ShowOverlay` - last decision on screen

## Logs

`BepInEx\Dd2Autobattler\logs\<timestamp>\decisions.jsonl`

Each turn has actor state, both sides, legal actions with scores, and the chosen skill/target/reason. Session report:

```
powershell -File _tools\analyze-log.ps1
powershell -File _tools\analyze-log.ps1 -Today
```

## Issues and suggestions

If you use this and it does something dumb, throws a fight, or you have an idea to make it play better: [open an issue](https://github.com/syyzit/dd2-autobattler/issues).

Please attach the relevant log when you can. The folder above is one file per session (`decisions.jsonl`). The fight that went wrong is enough; you do not need every run you ever played. A short note of what you expected vs what it clicked helps more than a screenshot of the overlay.

## License

MIT. Darkest Dungeon II is © Red Hook Studios. This project is unofficial and ships no game assets.
