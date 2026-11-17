# DD2 Autobattler

Experimental **BepInEx 5** plugin that plays the hero turns of [Darkest Dungeon II](https://www.darkestdungeon.com/) combat for you. You still drive the map, the inn, hero selection and gear; the bot only clicks skills, targets and combat items during fights.

Not an official Red Hook mod. **Not a Steam Workshop item** (Workshop mods are data/CSV only; this plugin patches the running game). Game updates can break it. It will throw fights.

## What it does

Every hero turn it reads the game's own skill previews and scores each legal skill, target and combat item, then clicks the best one.

- **Scoring from the live preview:** damage, kill chance, heals, tokens and DoTs as the game previews them, with resists and hit chance. A real hit beats a 0-damage Combo mark; a big hit is not dumped into a 1 HP enemy that an ally will finish first; early rounds pay more for setup (Combo, stun, DoT, Strength).
- **Cited boss and fight rules:** named fights (Dreaming General, Shackles of Denial, Harvest Child, Chirurgeon, Librarian, Seething Sigh, Focused Fault, Leviathan, Cultists / Exemplar, Ravenous Reach, Body of Work, Implication, Cabin Boy, Shambler, Tangle mashes) follow rules taken from the game data and the official wiki's Strategy sections, and nothing else. See [`notes/cited-bosses.md`](notes/cited-bosses.md).
- **Hero resource loops:** Hellion Winded, Leper Blind / Ruin, Flagellant Pain, Jester Finale and extra action, Vestal Conviction, Duelist stances, Abomination beast form, Combo reach, DoT partners and rank occupancy. See [`notes/cited-classes.md`](notes/cited-classes.md) and [`notes/class-play.md`](notes/class-play.md).
- **Death's Door:** a hero on Death's Door gets a skill heal, then food / bandage / antivenom; a healthy Man-at-Arms guards an ally on Death's Door instead of a weak swing; a hero on Death's Door does not swing into a Riposte.
- **Items as free actions:** combat items cost no turn (game data `m_IsFreeAction`), so heals, stress items, cleanses and DoT items are used alongside the hero's skill when they are worth it.
- **Rank walks:** a healer who cannot launch a heal, or a damage dealer who cannot reach the target that matters, walks onto a launch rank, without shoving an ally who already reaches it off their tile. Corpses blocking the front are cleared with Pouch of Lye or a clear skill.
- **Last-enemy recovery:** when one safe enemy is left, a turn that would be idle (a pass, a losing support) heals HP, strips a DoT or heals stress instead. It never delays the kill. See [`notes/recovery.md`](notes/recovery.md).
- **Optional round lookahead:** off by default. See [`notes/search.md`](notes/search.md).

Playtested mostly with the starter party (Man-at-Arms / Highwayman / Grave Robber / Plague Doctor). Other heroes use the same scorer plus their resource-loop rules.

## Modes

The overlay in the top-left corner has two buttons:

- **AUTO** - the bot clicks every hero turn.
- **SHADOW** - you click; the bot logs what it would have picked and how your click compares.

Switching is live, no restart. Story and shrine fights are left to you (the overlay says so). The bot never retreats.

## Install

1. Install [BepInEx 5](https://github.com/BepInEx/BepInEx/releases) (x64, Mono build) into the Darkest Dungeon II game folder and start the game once so BepInEx creates its folders.
2. Put `Dd2Autobattler.dll` into `BepInEx\plugins`. Take it from a release of this repository if one is published, or build it yourself (below).
3. Start the game. The AUTO / SHADOW overlay appears top-left.

To uninstall, delete `Dd2Autobattler.dll` from `BepInEx\plugins`.

## Config

`BepInEx\config\drednot.dd2.autobattler.cfg` (created on the first start):

| Key | Default | What it does |
|---|---|---|
| `Combat.Enabled` | `true` | The bot scores hero turns. Off = you play and no bot pick is logged. |
| `Combat.ShadowMode` | `false` | With `Enabled`, you click and the bot only logs what it would have clicked. The overlay buttons set this live. |
| `Combat.LastEnemyRecovery` | `true` | Last-enemy recovery (above). |
| `Logging.LogPreviews` | `true` | Include every scored action and the full turn snapshot in the log. |
| `UI.ShowOverlay` | `true` | Show the AUTO / SHADOW buttons and the last decision on screen. |
| `Search.Mode` | `Off` | Round lookahead: `Off`, `Compare` (the bot still plays its normal pick and logs the lookahead pick under `search`), `On` (the lookahead pick plays when it beats the normal pick by `MinGain`). |
| `Search.Margin` | `45` | The lookahead only considers picks within this many points of the normal pick. |
| `Search.MinGain` | `4` | In `On` mode, how much better the lookahead pick must be. |
| `Search.NodeBudget` | `600` | Simulated actor turns per decision. |
| `Search.HardCapMs` | `8` | Time cap per decision; past it the normal pick plays. |

## Logs and bug reports

Each game session writes one file:

```
BepInEx\Dd2Autobattler\logs\<timestamp>\decisions.jsonl
```

It holds `fight_start` / `fight_end` records, one `turn` record per hero turn (both sides, legal actions with their scores, the chosen skill, target and reason), an `action_result` per resolved skill, and in Shadow mode a `shadow_result` line comparing your click with the bot's pick. The overlay shows the pick plus the runner-up.

If it does something dumb, throws a fight, or you have an idea to make it play better: [open an issue](https://github.com/syyzit/dd2-autobattler/issues). Please attach the session's `decisions.jsonl` (the fight that went wrong is enough) and a short note of what you expected vs what it clicked. The file names no folder on your disk: its first line gives its location relative to the `BepInEx` folder, and error lines have the `BepInEx` and user profile folders replaced.

## Build from source

Requirements: Darkest Dungeon II with BepInEx 5 installed, and a .NET SDK that can target `net472`.

Copy `Directory.Build.props.user.example` to `Directory.Build.props.user` and set `GameDir` to your game folder (or set the environment variable `DD2_GAME_DIR`). That file is gitignored. The build references the game's and BepInEx's assemblies from there.

```
dotnet build src\Dd2Autobattler\Dd2Autobattler.csproj -c Release
```

A successful build copies `Dd2Autobattler.dll` into `BepInEx\plugins`; add `-p:SkipGameCopy=true` to build without copying. Restart the game after every build (there is no hot reload).

### Tests

```
dotnet test src\Dd2Autobattler.Tests -p:SkipGameCopy=true
```

The tests are unit tests of the decision code plus golden cases in `src\Dd2Autobattler.Tests\Golden`: recorded turns with the pick the bot must (or must not) make.

### Replay

`src\Dd2Replay` re-decides logged turns with the current code, offline, without the game running or the DLL being deployed:

```
dotnet run --project src\Dd2Replay -- --all                                  # every session: agreement + changed picks
dotnet run --project src\Dd2Replay -- --all --save-baseline base.json        # before a change
dotnet run --project src\Dd2Replay -- --all --baseline base.json             # after: which decisions moved
dotnet run --project src\Dd2Replay -- --pin "<session>#<fight>/t<turn>" --name my_case --note "why"
```

`--help` lists the other options (`--since`, `--fight`, `--hero`, `--turn`, `--search`, `--validate-model`, `--json`, ...). Turn keys are `<session>#<fight no>/t<turn_index>`. `--pin` writes a golden case to `src\Dd2Autobattler.Tests\Golden`, which `dotnet test` then runs. Older logs without turn snapshots replay in a degraded mode; the summary lists what was approximated.

## License

MIT. Darkest Dungeon II is © Red Hook Studios. This project is unofficial and ships no game assets.
