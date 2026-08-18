# Cited boss notes

A rule ships only if it comes from this stack, in order:

1. Game CSVs / in-game data — the fact
2. [Official wiki](https://darkestdungeon.wiki.gg/) Strategy — the intent
3. Grand Slam / Infernal writeups — only when the wiki is thin and they agree
4. Our logs — only to check we implemented the cited rule

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
  - Health (Padlock of Wasting) first — wiki option A. Death heal is wasted while the others are full.
  - Then whichever of Melee / Ranged denies more of *this* party’s attack tags (wiki: “whichever Lock denies the most damage from your team”).
  - Stress (Shackle of Despair) last.
- **Not encoded:** pass / buff when Health is not a legal target this turn. Sources do not agree on that fallback.

## Tangle / Lost Battalion mashes

- **Sources:** [The Tangle](https://darkestdungeon.wiki.gg/wiki/The_Tangle) — Bishops resurrect via Benediction; Drummers grant Order and move-resist, “killing the Drummer is a high priority.” CSV: Bishop `serve_once_more`; Drummer `death_before_dishonor`.
- **Rules encoded:** Bishop `must_kill` first; Drummer `commander` next; Knight / foot / arbalist deferred while either is a legal target.

## How to add the next boss

Copy the wiki Strategy paragraph into this file, quote the CSV keys that back the numbers, then encode only those sentences. If a live log looks drunk, write the mismatch here — do not invent a new rule from the log.
