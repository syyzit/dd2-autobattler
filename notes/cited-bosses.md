# Cited boss notes

A rule ships only if it comes from this stack, in order:

1. Game CSVs / in-game data - the fact
2. [Official wiki](https://darkestdungeon.wiki.gg/) Strategy - the intent
3. Grand Slam / Infernal writeups - only when the wiki is thin and they agree
4. Our logs - only to check we implemented the cited rule

Do not encode a fallback that only we invented.

## Dreaming General

- **Sources:** [wiki Strategy & Advice](https://darkestdungeon.wiki.gg/wiki/Dreaming_General#Strategy_&_Advice). CSV: Taproot `m_IsHealthless`; vine tokens `taproot_tangle_a` / `_b` / `_c`.
- **Rules encoded:**
  - Round 1: ignore Taproot, hit the General.
  - Even rounds: hit Taproot once.
  - Odd rounds after 1: one Taproot hit per living hero with more than one vine (`_b` or `_c`).
  - If someone is already at `_c` (Nightmare lock), allow one Taproot hit that round so the root still retracts. Wiki: Nightmare is almost never seen if you keep hitting the root on schedule.
  - After the round budget is spent, defer Taproot and hit the General.
  - Never use `taproot_tangle_*` hero skills if a real skill is legal.
- **Not encoded:** “slap Taproot whenever anyone has vine B/C” (our v1/v2 guess). The wiki is a scheduled cadence, not an emergency-only slap.

## Shackles of Denial

- **Sources:** [wiki Strategy](https://darkestdungeon.wiki.gg/wiki/The_Shackles_of_Denial#Strategy); [r/darkestdungeon 19348th](https://www.reddit.com/r/darkestdungeon/comments/19348th/tips_to_fight_shackles_of_denial/) (focus one; primary damage type; Stress last; do not finish Health beside a wounded lock). CSV: `lock_*` skill blocks; Health death `m_HealthHealPercent` 0.33; Melee death +20% dmg; Ranged death +10% crit; Stress death 10% Blind/Weak on hit.
- **Rules encoded:**
  - Focus one lock. Current pick is `must_kill`; others deferred if the pick is a legal attack.
  - Health (Padlock of Wasting) first - wiki option A. Death heal is wasted while the others are full.
  - Then whichever of Melee / Ranged denies more of *this* party’s attack tags (wiki: “whichever Lock denies the most damage from your team”).
  - Stress (Shackle of Despair) last.
- **Not encoded:** pass / buff when Health is not a legal target this turn. Sources do not agree on that fallback.

## Tangle / Lost Battalion mashes

- **Sources:** [The Tangle](https://darkestdungeon.wiki.gg/wiki/The_Tangle) - Bishops resurrect via Benediction; Drummers grant Order and move-resist, “killing the Drummer is a high priority.” CSV: Bishop `serve_once_more`; Drummer `death_before_dishonor`.
- **Rules encoded:** Bishop `must_kill` first; Drummer `commander` next; Knight / foot / arbalist deferred while either is a legal target.

## Harvest Child (Foetor lair)

- **Sources:** [wiki Strategy](https://darkestdungeon.wiki.gg/wiki/Harvest_Child#Strategy_&_Advice). CSV/classes: `plague_eater_harvest_table` (Child), `*_fetid_meat`, `*_putrid_meat`. Hunger token `harvest_hunger`; rank 1 is forced `harvest_hunger` / Feed the Hunger (-15% max HP, unresistable).
- **Rules encoded:**
  - Focus the table. Both meats are deferred while the table is alive. Do not finish a meat if the table is a legal attack.
  - If another hero has Harvest Hunger and this hero does not, prefer MAA Hold the Line / Hellion Toe to Toe (wiki: take rank 1 and immobilize so the hungry hero is not pulled into the eat). Those skills are not vetoed just because they hit a meat.
  - If this hero is hungry, those same forward-moves are penalized (they walk you into rank 1).
- **Not encoded:** inn prep (Apples and Cheese, antivenom). Forced eat on rank 1 cannot be skipped. Immobilize on the meats themselves (Leper Bash / net) is not wired.

## Relationship curses

- **Sources:** CSV `skill_modifier_data_export` - negative relationships set `m_IsForceEquip` on the cursed skill (`hateful_a`, `suspicious_a`, `envious_a`, `resentful_*`). Using it applies the rider to the partner (Vulnerable, Taunt, stress, Blind/Weak).
- **Rules encoded:** if a cursed attack/support is legal and a non-cursed attack is also legal, the cursed skill is penalized (-40). Crisis heals are never penalized. If the cursed skill is the only hit on the focus target, it still wins.

## Healer rank

- **Sources:** Battlefield Medicine `launch_ranks` 3,4; `m_Limit` 3. Hero `*_move` is adjacent swap (`m_IsMoveToTarget`, relative -1/+1).
- **Rules encoded:** if an ally is in crisis (Death's Door or <=35%) and no skill heal is legal, allow a move that lands the healer on a launch rank of an equipped heal that still has uses. Do not un-veto every swap.
- Cleanse items that also heal (antivenom, bandage) are spent on Death's Door / <=30% instead of being scored as a wasted cleanse.
- If the skill heal is spent (`m_Limit`) and someone is on Death's Door or <=30%, food/bandage/antivenom that still heal are forced. A last-enemy kill does not skip a Death's Door heal. Support (Ounce of Prevention) is penalized while the party is in crisis.
- Last living enemy behind corpses: if no damaging attack is legal, walk onto a launch rank of an equipped attack that can hit that rank. 0-damage Combo marks do not count as a hit. Do not un-veto every swap.
- Combat items are classified from CSV conditions/tags, not a name list: `target_is_corpse_hidden` (Pouch of Lye), `target_has_*_dot` (DoT cleanse), `target_is_diseased` (Single Leech), `target_has_blind` (Rag), `stress_heal`, `heal`. Enemy items stay attacks (witchbane). Grenades that click a corpse still skip. Buffs (powders, war horn, The Blood) stay on the token/buff scorer — do not dump them.

## Chirurgeon (Gaunt table)

- **Sources:** [wiki Strategy](https://darkestdungeon.wiki.gg/wiki/Chirurgeon#Strategy). CSV: `shared_lost_soul_chirurgeon`, `shared_lost_soul_patient`, `_widow`, `_yeoman`. Leucotomy heals 33% and buffs patients each round. Boss-node modifier otherwise marks every gaunt as a boss.
- **Rules encoded:**
  - While the Chirurgeon is up, he is `must_kill`. Patients / Widow / Yeoman (and other lost-soul / gaunt packmates) are deferred adds; their boss flag is cleared.
  - Wave 1 with no Chirurgeon is unchanged (Yeoman still scores as large support).
- **Not encoded:** inn prep, DoT-then-Cause-of-Death, Trepanation negative-token bait.

## Librarian (Sprawl lair)

- **Sources:** [wiki Strategy](https://darkestdungeon.wiki.gg/wiki/Librarian#Strategy). CSV: `fanatic_librarian` / `_ignited`, stacks `fanatic_librarian_stack_l` / `_m` / `_s`. Page Burner `librarian_books_destroyed` extra action; Ignite when no books left.
- **Rules encoded:**
  - Focus the Librarian. Book stacks are deferred while he is up, including when he is out of reach — support/pass rather than punch a stack.
  - Do not finish a stack. Wiki: destroying books lets him Ignite sooner and grants a free party-wide Burning Bright. Kill him before the stacks are gone (about 6-7 rounds of his own Page Burner).
  - A self-heal rider on an enemy attack (Crush on Combo) is still an attack, so MAA can actually hit him.
  - AoE that splashes a stack (Flashing Daggers) is penalized the same as clicking the stack.
- **Not encoded:** knockback to slide books behind him; Categorize alphabetical reorder; ignite-phase item/Burn-salve inn prep.

## Seething Sigh

- **Sources:** [wiki Strategy & Advice](https://darkestdungeon.wiki.gg/wiki/Seething_Sigh#Strategy_and_advice). CSV: `boss_lungs_core`, `boss_lungs_front`, `boss_lungs_back`; inflate token `lung_inflate` / `lung_inflate_front` / `lung_inflate_back`; exhale `lungs_core_exhale` clears inflate. Wiki: 6% max HP pops inflate (front 12, back 9).
- **Rules encoded:**
  - If a living lung has `lung_inflate`, hit that lung. The core is deferred while an inflated lung is a legal attack. Clear at least one token; double-token Sundering Exhalation is the wipe.
  - If no lung is inflated, hit the core. Uninflated lungs are deferred.
  - Do not finish a lung when a non-kill pop is legal, or when the lung has no inflate. Wiki: dead lungs make Wrath/Hysteria multi-target; "rarely recommended to kill the lungs"; "preferable to kill a lung than to withstand a Sundering Exhalation."
- **Not encoded:** inn prep (Apples and Cheese, Restorative Herbs). Hero-specific lung-pop skill picks. Bastard's Beacon modifiers.

## Focused Fault

- **Sources:** [wiki Strategy](https://darkestdungeon.wiki.gg/wiki/Focused_Fault#Strategy). CSV: `boss_eyes_stalk_l` / `_m` / `_s`, `boss_eyes`; Seen token `eyes_focus`. Limerence +8 DMG per Seen; Suppress requires ≥3 positive tokens on a hero without Seen.
- **Rules encoded:**
  - Phase 1 (`eyes_stalk_*`): every stalk is `must_kill`. Kill them; they split Cluster → Bifurcated → Cloistered.
  - Phase 2 (`boss_eyes`): the mass is `must_kill`. Weak on the mass is extra (wiki: Weak/Block blunt Limerence).
  - Heal/guard the hero with `eyes_focus`.
  - Do not stack a third positive token onto a hero who is not Seen while the mass is up (wiki: Suppress on ≥3 positives).
- **Not encoded:** inn prep, outspeed-then-Taunt plan, Dodge-to-avoid-Seen plan. Those are inn/speed, not a targeting rule.

## Ordainment

- **Sources:** [wiki Ordainment](https://darkestdungeon.wiki.gg/wiki/Ordainment). HP/DMG (and confession-specific) buffs on trash as the mountain run goes on. Act/lair bosses are never ordained except Bastard's Beacon.
- **Rules encoded:** none. Extra HP and damage already sit in the snapshot and in `QuerySkillPreview`. The wiki has no targeting rule ("kill ordained first" is ours).
- **Not encoded:** confession-specific on-crit token copy, Block→Block+ conversion, invert-on-crit. No combat click follows from those sentences.

## How to add the next boss

Copy the wiki Strategy paragraph into this file, quote the CSV keys that back the numbers, then encode only those sentences. If a live log looks drunk, write the mismatch here - do not invent a new rule from the log. Remaining Confession bosses: Ravenous Reach, Body of Work.
