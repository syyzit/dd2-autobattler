# Class play and party synergy

Architecture for playing heroes beyond the playtested PD / GR / HWM / MAA four. Combat only. You still hire, gear, and pick inn skills.

## Layers

1. **Generic scorer** — live preview damage/kill/heal plus CSV tokens. Every hero. Performer-side tokens (Winded, Ruin strip) come from `PERFORMER*` effect groups; heal-kind uses CSV tag `heal` plus an actual HP heal effect (More MORE! is tagged heal and is not a heal).
2. **Kit-safety / class play** (`KitSafety`) — unique resource loops: generate, hold, spend. Do not fight the kit.
3. **Party synergy** (`PartyKit` / `PartySynergy`) — Combo reach, DoT partners, rank occupancy.
4. **Boss notes** (`notes/cited-bosses.md`) — fight-side targeting. Unchanged.

Do not add a `HellionAi` class. Extra code exists only when the generic scorer misuses a unique resource, or when a friendly setup skill loses to any swing.

A rule ships only from `notes/cited-classes.md` (CSV + wiki Strategy when a sentence exists). DD2 class Strategy pages are often stubs; CSV conditions are the rule (`hel_howling_end` requires `performer_has_3_winded`). Logs check the implementation. Do not encode path “best builds.”

## Unique resource loops

| Loop | Generate | Hold | Spend |
|---|---|---|---|
| Winded | Hack / Swan / Bleed Out / Breakthrough | no Adrenaline while healthy and under 3 | Howling End at 3; Bloodlust as the other dump |
| Blind / Ruin | Ruin while not ready | Reflection if Blind | Chop/Hew; Combo still ignores Blind |
| Pain | More MORE! / Punish | no crisis-heal until Death's Door | Redeem when actually dying |
| Finale | Razor's Wit Combo | not a chip cleaner | Finale on a real HP bar or a kill |
| Stance | first Meditation/Preparation | do not recast the same stance | matching attacks (Fleche stays an attack) |
| Beast | Transform if not beast and no party Death's Door | do not Revert while healthy | Revert only to heal |

## Party synergy (in combat)

Already shipped: Combo apply if a spender still acts; save Combo for a better spender; Bleed/Blight/Burn +4 if a partner inflicts that DoT; protect the unique healer.

Phase 1 adds:

- **Combo reach** — do not pay `apply_combo` on a rank no remaining spender can hit.
- **Rank occupancy** — two rank-1 bodies (Hellion + Leper, Hellion + MAA) must not Toe-to-Toe / Hold the Line each other out of rank 0. Harvest hunger walks stay an exception.

Out of scope here: inn hire advice, trinket pairs, skill loadouts. A HUD line (“no rank-1 tank”) is a later optional surface.

## Phases

- **Phase 1:** Winded / Blind-Ruin / Pain generate-spend, Combo reach, rank occupancy.
- **Phase 2:** Jester extra action (`the_last_laugh`), Vestal Conviction, DoT host, Runaway Hearthlight/Firestarter.
- **Phase 3:** Duelist first stance pay, Abom form-match, heal classify for Comfort / Battle Heal / Cauterize / Absolution. Holy Lance is a back-rank attack that walks you forward once — no extra veto (launch ranks 3–4 drop it after the walk).
- **Phase 4 (not done):** path HP thresholds, recruit overlay.

Playtest one Confession region before the next phase. Restart the game after every build.
