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

## Librarian (Sprawl lair)

- **Sources:** [wiki Strategy](https://darkestdungeon.wiki.gg/wiki/Librarian#Strategy). CSV: `fanatic_librarian` / `_ignited`, stacks `fanatic_librarian_stack_l` / `_m` / `_s`. Page Burner `librarian_books_destroyed` extra action; Ignite when no books left.
- **Rules encoded:**
  - Focus the Librarian. Book stacks are deferred while he is a legal attack.
  - Do not finish a stack. Wiki: destroying books lets him Ignite sooner and grants a free party-wide Burning Bright. Kill him before the stacks are gone (about 6-7 rounds of his own Page Burner).
- **Not encoded:** knockback to slide books behind him; Categorize alphabetical reorder; ignite-phase item/Burn-salve inn prep.

## How to add the next boss

Copy the wiki Strategy paragraph into this file, quote the CSV keys that back the numbers, then encode only those sentences. If a live log looks drunk, write the mismatch here - do not invent a new rule from the log.
