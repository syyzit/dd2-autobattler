# End-of-fight recovery

Same stack as `cited-bosses.md`: game data for the fact, logs to size it. Question: once a fight is won,
should heroes drag it out to heal before the kill? Answer from the data: almost never worth it. What ships
is the smallest safe part — idle turns on the last enemy become recovery; nothing delays the kill.

## Game limits (CSV `StreamingAssets/Excel`, IronCrown.dll)

- **Battlefield Medicine** (`hero_pd`): `m_Limit,3` per fight (logs: `LimitedSkills` lists it after 3 uses in a
  fight, 84 turns). Legal when the target is under 50% HP or has a bleed/blight/burn
  (`target_meets_heal_threshold_high_hidden` = health_percent < 0.5, `target_has_*_dot_hidden`). Heals 20%
  (`_u` 25%) of max HP only under 50% (`heal_2*pct_target_threshold_high`), always strips all DoTs
  (`remove_all_dots`). `m_IsStallInvalidating,False`.
- **Artemisia** (`hero_gr`): `m_Limit,3`; self-heal 33% only under 33% HP (`heal_33pct_self_threshold_med`);
  always +3 Dodge, +1 Speed.
- **Bolster** (`hero_maa`): `m_Cooldown,1` (logs: never legal the next round, legal two rounds later). Target
  stress heal 1 (`_u` 2) only at stress >= 5 (`target_at_stress_heal_threshold`); the MAA heals himself 1 (`_u` 2)
  only at >= 50% stress (`performer_at_stress_heal_threshold`).
- **Passes** (`pass_data`): `pass_stress` = `stress_damage_2` on the passer (+2 stress, no upside).
  `pass_heal` needs performer HP < 25%, `m_Cooldown,3`, heals 20% and adds 2 stress.
- **Ounce of Prevention**: buff only, cooldown 3 (`_u` 2). Not recovery.
- **Stall** (`combat_rules`: `m_StallMinRound,3`, `m_StallMinNumberOfHeroes,3`, `m_StallMaxNumberOfEnemies,2`,
  `stall_effects,stall_torch_loss` = torch -10; per-hero / single-hero lists are empty). IL
  `BattleTracking.GetIsRoundStall` / `EndStallRound` / `HandleEventSkillFinalizeResults`: from round 3, a round
  with 3+ heroes and 1–2 enemies costs 10 torch unless some skill used that round has
  `m_IsStallInvalidating,True` (one is enough: it clears `m_StallIsCurrentRoundValid`). Every damaging starter
  skill is True; heals, Bolster, guards, Take Aim, Tracking Shot, Blinding Gas and all enemy skills are False.
  Boss, lock, Librarian, Harvest Table and herostory classes set `m_IsStallInvalidating` on the class (no stall
  in those fights). Torch -10 is real: lower torch bands cost deathblow resist and add hero Blind
  (`torch_levels_data_export`).
- **Battle end**: `m_BattleEndMinHealthHealUpToPercent,0.2` — heroes under 20% are topped up to 20% for free.
- **No round clock** for regular enemies: no stress-per-round trigger (`stress_triggers`: crit, Death's Door,
  death only), round conditions only on Bishop, story fights and boss scripts. Crits still cost stress (80% on
  the target, 15% on each observer).

## What the remaining enemy does with extra rounds (CSV + logs)

Logged damage per enemy turn (starter party): arbalist 6.3 HP + 0.4 stress,
foot soldier 6.1, knight 11.7, herald 3.3 + 0.8 stress, immolatist 3.5 + 0.6, widow 1.4, carrion eater 1.6,
lost soul 3.0, spiders 2–2.6; ignited fanatics 11–41. The "soft" ones still cost something per turn:

- whipper / flayer / blind shaman have `*_ignite_self` (become the 11–41 damage `_ignited` forms);
- carrion eater `consume` corpses (extra action) then `evolve`; immolatist `funeral_pyre` heals on a corpse;
- herald / cherub / evangelist `worship` heal 30% and strip DoTs; herald Clarion Call is 2 stress;
- widow I Was Pretty is `horror_dot_medium` (3 ticks of 75% for 1 stress) — more than Bolster restores;
- spider / carrion / dog / lost soul hits roll diseases (5–10%, `*_disease`), which last the run.

## Size of the opportunity (logs)

- Many won fights end with a hero under 50% HP or at 5+ stress, and about two thirds of those have a safe
  last enemy (no boss / summon / revive / must-kill flag, not a boss fight) with some recovery still legal.
- Holding the kill there (Medicine while charges last, Bolster every other round, Artemisia under 33%) for up
  to 3 extra rounds restores less HP than the enemy deals in those rounds — net negative HP. Only against
  enemies at <= 5 damage a round does it come out ahead, by about 3 HP a run, before torch, diseases and
  worship heals. Medicine charges are often spent by then.
- The real waste is idle turns: last-enemy turns where a recovery click was legal and something else was
  taken, mostly the MAA guarding at -35 or passing (`pass_stress`, +2 stress to himself) while Bolster on a
  5+ ally scored -65.

## Rules encoded (`TurnDecider.RecoverOverIdle`, config `[Combat] LastEnemyRecovery`, default on)

- Exactly one living enemy, it has HP, no Boss / Summons / Resurrects / MustKillFirst / Commander flag, no
  scripted boss mode (Denial locks, Exemplar, Leviathan hand, Reach phases, Cabin Boy burst, Chirurgeon), not
  `_ignited`. No hero on Death's Door (the crisis gates own that).
- Only when the pick is idle: a pass other than `pass_heal`, a support the scorer rates below 0, or a
  0-damage click on the enemy (Blinding Gas, Tracking Shot). Picks with a cited reason are left alone.
- Then take the legal click that restores most: preview HP heal, Medicine on a hero with a DoT, or a stress
  heal on a 5+ target (plus the MAA's own 5+). 1 stress = 4 HP. With an ally at 35% HP or lower a guard is
  kept; a pass is still replaced.
- Reason `recover`. Damaging hits and kills are never replaced, so no round is added: no stall torch, no
  extra enemy turn, no round cap needed.

- `cultist_mountain_*` (last fight before the mountain boss, `TurnSnapshot.FightId`): the Deacon is the last
  enemy in most logged fights and is Boss + must-kill, so the rule above never fired there. In that fight
  a pass or a filler support (not a guard, not a 0-damage Blind / mark, not a DoT) on the last enemy becomes
  recovery whatever the enemy is. Bolster over a non-kill swing there is `mountain_bolster`
  (`notes/cited-bosses.md`, Cultists). Holding the kill is still not encoded: the last enemy there is the
  stress dealer.

## Not encoded

- Holding the kill to heal (the measured gain is ~3 HP a run against torch, diseases and stress).
- Swapping a damaging non-kill chip for a heal (delays the kill by a turn; same trade as above).
- The stall round number base (0 or 1) was not checked in game; it only matters for holding, which is off.
