# Round lookahead (Search)

Optional layer on top of the greedy decider. Config `Search.Mode`: `Off` (default), `Compare` (greedy plays; the search pick, value and line are logged under `search` in the turn record), `On` (the search pick plays when it beats greedy by `MinGain`). Code: `src/Dd2Autobattler/Search/`.

## What it does

1. Greedy decides as before (`TurnDecider.DecideSnapshot`). `PickAction` now reports which gate returned the pick and which candidates survived every cited veto.
2. The search runs only when the pick came from the plain attack / heal competition (`attack`, `attack_over_idle`, `any`, `heal_beats_attack`, `any_over_negative_attack`). Crisis heal, cultist defense, artillery Blind, tank Riposte, corpse reach, rank walks, setup, free items and the hunger eat are cited forces: never searched.
3. Root alternatives: vetted candidates of the same class as the greedy pick (attack on a living enemy vs attack, HP heal vs heal), within `Margin` (45) of the greedy score, at most `MaxRoot` (6). Heal-vs-attack switches stay with greedy (crisis-heal, Pain/Flagellant and idle-vs-connecting-hit gates decide those).
4. Each alternative: apply the game's exact preview, then play the rest of the current round in the game's turn order (`RemainingTurns`). Value = greedy score + `FutureWeight` x (board value at round end - board value right after the action). Greedy stays the leaf heuristic for the action itself; the search only adds what happens later in the round.
5. Deterministic: expected values, no randomness. Budget `NodeBudget` (600 simulated actor turns, the greedy pick is always evaluated first); `HardCapMs` (8 ms live) abandons the search and greedy plays.

## Forward model (`ForwardModel`, `SimBoard`, `GameData`)

| Covered | How |
|---|---|
| Performer's action | Game preview: per-hit damage and roll range, hit chance, heal, tokens on every body a MultiHit skill hits, DoT magnitude x land, CSV moves |
| Kill / survive | Probability over the integer roll range; HP variance carried from earlier expected hits (normal approximation) |
| Death's Door | Heroes: probability of Death's Door, deathblow at (1 - death resist). Enemies: Death's Door armor tokens absorb one hit each, then deathblow |
| Corpses / ranks | `<class>_corpse` in the CSVs leaves a corpse in place; otherwise positions behind shift up. Team position is ordinal; CSV ranks are slots (size-2 bodies count twice) |
| Turn order | Game's remaining order this round; dead and stunned actors lose their turn (stun spent) |
| DoT / regen | Tick at turn start, before acting |
| Later heroes | CSV damage range x Strength/Weak x target Block/Block+/Vulnerable, hit x Dodge/Blind, CSV launch/target ranks (game masks when the snapshot has them). One-step policy (kill value, focus weight, heals only at or below 50% HP, Flagellant only at or below 25%); top `AllyBranch` (2) options branched at the first ally turn; landed at `AllyTrust` (0.75) |
| Enemies | Uniform over usable skills (launch rank, a legal target, no `performer_has/is` conditions), uniform over legal targets, Taunt narrows single-target attacks, Stealth hides. Expected damage, tokens, DoT, moves |
| Board value | Enemy HP removed and kills (x focus weight; kill adds `KillBonus` + `ThreatRounds` x the enemy's expected damage per turn); hero expected HP lost (x `LowHpWeight` below 35%), `DoorPenalty` x Death's Door probability, `DeathPenalty` x death probability |

Ignored (flagged in `unmodeled` where met): summons and splits (Focused Fault stalks, Cabin Boy), class changes, extra turns, Riposte, Guard redirection, enemy AI weights and phase logic, trinkets / quirks / path stats / ordainment / buffs for non-performers, stress, anything past the end of the round (next round's order is a speed roll: CSV speed orders only about 73% of actor pairs correctly in logs, so it is not used).

## Measured accuracy

`dotnet run --project src\Dd2Replay -- --since <yyyymmdd> --validate-model` predicts board N+1 from board N + the logged pick + the actors that acted in between, on logs that carry `action_result`. On the first logs measured (217 consecutive turn pairs, acting order taken from the log):

| Field | Model (EV enemy policy) | Oracle actions (true skill + target, model numbers) | No-change baseline |
|---|---|---|---|
| Enemy HP MAE (all / changed only) | 0.31 / 1.68 | 0.26 / 1.35 | 0.69 / 4.53 |
| Hero HP MAE (all / changed only) | 1.45 / 4.11 | 0.98 / 3.10 | 1.94 / 6.87 |
| Enemy alive | 98.1% | 98.1% | 97.5% |
| Enemy rank | 92.2% | 94.2% | 88.8% |
| Hero Death's Door | 99.2% | 98.9% | 98.5% |
| Hero rank | 95.7% | 96.7% | 95.7% |

Per component:

- Own action: preview damage on hit MAE 1.58 HP, no bias; 97.8% inside the preview range (or a crit). Model kill call 97.5% correct (Brier 0.08), versus 85.2% for the decider's kill flag: the decider counts a hit on a 0 HP Death's Door enemy as a kill, but Death's Door armor absorbs it.
- Enemy damage (true skill and target): MAE 1.30, bias -0.58 (CSV under-reads by ~13%: ordainment / buffs); 87% inside the CSV range.
- Enemy choice: the actual skill is in the model's usable set 94.7%, the actual target in its legal set 96.9%. The model gives the actual skill p=0.53 and target p=0.66 on average (uniform; the real AI is not in the data).
- Ally (CSV) damage vs the game's own preview for the same hero: MAE 1.83, 68% within 2 HP, 56% within 25%. Worst: Leper Chop +2.3 over, Leper Purge -2.1 under.
- Missed enemy deaths are mostly DoT deaths of enemies that never acted, so they are absent from the observed order. Logs with `snap.RemainingTurns` give the `ev_model_game_order.*` rows instead.

## Search vs greedy on real logs

`dotnet run --project src\Dd2Replay -- --since <yyyymmdd> --search` lists the turns where search and greedy disagree, with both lines. On the first logs reviewed the search ran on about a quarter of the turns (the rest are forced gates, non attack/heal picks, single options) and disagreed on about one in eight of those; mean 0.5 ms per searched decision on the desktop CLR. Reviewed against the notes, the search was better where it saw what happens later in the round: Iron Swan on a gaunt that dies to its own blight tick before it acts (pinned: `search_dot_finish_before_it_acts`), Blinding Gas on the enemy that still hits a low hero this round rather than one that has already acted (pinned: `search_blind_the_attacker`), leaving a chip for the ally who acts next. No reviewed disagreement favoured greedy.

## Not verified

- The game's real `RemainingTurns`, enemy equipped skills and hero kits in a live snapshot are untested, and so is whether `RemainingTurns` lists the current actor first (handled either way).
- No boss fight has been measured with turn order logged: the boss hypothesis is untested.
- Mono timing in the game frame is not measured (desktop CLR only). CSV load (553 files, 6 MB) happens once at plugin start when `Mode` is not `Off`.
- `On` mode has never played a fight.

## Tuning

Config `[Search]`: `Mode`, `Margin`, `MinGain`, `NodeBudget`, `HardCapMs`. Replay overrides: `--search-margin`, `--search-gain`, `--search-branch`, `--search-budget`, `--search-cap`, `--search-weight`, `--search-trust`; `--search-on` reports override semantics, `--verbose` adds the greedy line, `--json` for machine output. Other weights (`DoorPenalty` 40, `DeathPenalty` 250, `KillBonus` 15, `ThreatRounds` 2, `LowHpWeight` 1.5) are fields of `SearchSettings`.

Before trusting `On`: run `Compare` for a session, then `Dd2Replay --search` on it, review every disagreement, and re-run `--validate-model` there for `ev_model_game_order.*`. Pin wins with `--pin KEY --search --expect-search search`. A win-rate claim needs many runs; this layer only changes near-ties within `Margin`.
