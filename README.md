# DD2 Autobattler

Experimental **BepInEx 5** plugin that plays [Darkest Dungeon II](https://www.darkestdungeon.com/) combat. You still manage the map, inn, and gear.

Not an official Red Hook mod. **Not a Steam Workshop item** (Workshop is data/CSV only; this patches the live game). Game updates can break it. It will throw runs. Restart the game after every build - there is no hot reload.

## Status

Playtested on a Confession party (Plague Doctor / Grave Robber / Highwayman / Man-at-Arms). Other heroes use the same scorer plus kit-safety (Winded, Blind, Pain, Finale, Ruin, stances, Transform) so a replacement tank is not a dead click. It will throw fights.

Named fights use **cited notes** only (`notes/cited-bosses.md`: wiki Strategy plus game CSVs). Hero resource loops (Winded, Blind/Ruin, Pain, Combo reach, rank occupancy) are in `notes/cited-classes.md`. Architecture: `notes/class-play.md`. A rule ships if that file says so. Roaming bosses without a note use default scoring.

Each turn it scores legal skills from the live preview: damage, kills, heals, tokens, items. One ply of leftover HP so a real hit beats a 0-damage Combo mark. Combo on a 1 HP chip is skipped (Tracking Shot on a dying add is a wasted turn). If a later ally acts before that enemy, it will not dump a big hit into a 1 HP chip. Early rounds with a full pack pay more for setup (Combo, stun, DoT, Strength) than a chip. Unique kit tokens are CSV-backed: Hellion dumps Winded at 3 via Howling End; Leper does not Chop into Blind (Combo still ignores it); Flagellant is not crisis-healed until Death's Door; Jester Finale is not a chip cleaner; Abom transforms into beast unless someone is on Death's Door.

Death's Door still gets a skill heal, then food / bandage / antivenom. A kill on the last HP bar (not a healthless fixture like Taproot) can skip that rest to end combat. Crisis heals once per ally per round; Rest (`pass_heal`) does not spend that slot. A 0-damage Combo mark does not inherit must-kill focus (Tracking Shot on a Cherub is not a 138-point "boss" hit). Cursed relationship skills lose to a clean alternative. Stress support (Inspiring Tune, Bolster) scores like laudanum.

If a healer cannot launch their heal, they walk onto a launch rank (`pass_heal` is Rest, not a heal). If a damage dealer cannot reach the last living enemy or the current must-kill, they walk onto a launch rank of a damaging attack (a Combo mark does not count). That walk does not fire when an ally already hits that enemy from their current rank (otherwise two frontliners shove each other off the tile). Librarian still never swaps the hero who already punches him. Rank walks also fire when a cited note says this hero's tokens are illegal in this rank. Pouch of Lye or a clear skill (Leper Purge) clears a corpse, especially then. Token strip (Dodge, Riposte, Combo, Stealth) can beat a non-kill swing. AoE (Flashing Daggers) scores the sum of living HP it hits — a corpse in the cone is not extra damage. AoE that splashes a must-kill is not vetoed just because the click target is an add.

Overlay **AUTO** / **SHADOW** (top-left): Auto clicks for you. Shadow lets you click and logs what the bot would have picked. Stagecoach speed is the separate Gotta Go Fast plugin. Retreat is opt-in and off.

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

- `Combat.Enabled` - bot scores hero turns (off = you play, no log of a bot pick)
- `Combat.ShadowMode` - with Enabled, you click and the bot only logs what it would have clicked
- Overlay **AUTO** / **SHADOW** buttons (top-left) switch that live, no restart
- `Logging.LogPreviews` - include every scored action in the JSONL
- `UI.ShowOverlay` - toggles plus the last decision on screen

## Logs

`BepInEx\Dd2Autobattler\logs\<timestamp>\decisions.jsonl`

Each turn has actor state, both sides, legal actions with scores, and the chosen skill/target/reason. Shadow mode adds `mode: shadow` on the bot proposal and a `shadow_result` line when you confirm a click (`match`, bot vs human, score `gap`, human `rank` in the bot's list). The overlay shows the pick plus the runner-up (`next Thrown 152`). Session tools:

```
powershell -File _tools\analyze-log.ps1
powershell -File _tools\analyze-log.ps1 -Today
python _tools\analyze_log.py
python _tools\analyze_log.py --today
python _tools\analyze_log.py --since 20260823
python _tools\analyze_log.py --fight eyes
python _tools\analyze_log.py --fight eyes --quiet
python _tools\analyze_log.py --fight exemplar --hero grave_robber
python _tools\skill.py flashing_daggers
```

`--fight` dumps every turn (party, enemies, top legal scores) and prints cited-note mismatches (non-killing Flashing on stalks, laudanum over a stalk kill, Death's Door heal skip, punching an add while a controller is legal). `--cite` runs those checks on a whole session. `skill.py` looks up launch ranks / tags / path replacements in the game CSVs.

## Issues and suggestions

If you use this and it does something dumb, throws a fight, or you have an idea to make it play better: [open an issue](https://github.com/syyzit/dd2-autobattler/issues).

Please attach the relevant log when you can. The folder above is one file per session (`decisions.jsonl`). The fight that went wrong is enough; you do not need every run you ever played. A short note of what you expected vs what it clicked helps more than a screenshot of the overlay.

## License

MIT. Darkest Dungeon II is © Red Hook Studios. This project is unofficial and ships no game assets.
