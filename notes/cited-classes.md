# Cited class notes

Same stack as `cited-bosses.md`. A rule ships only if:

1. Game CSVs / in-game data — the fact
2. [Official wiki](https://darkestdungeon.wiki.gg/) Strategy — the intent, when a sentence exists
3. Logs — only to check we implemented the cited rule

Do not encode a fallback that only we invented. DD2 class Strategy pages are often stubs; then the CSV condition is the rule.

Unique **resources**, not hero names. Phase 1 is Winded, Blind/Ruin, Pain. Phase 2–3: extra action, Conviction, DoT host, Hearthlight, Firestarter, Beast form match, first stance.

## Winded (Hellion)

- **Sources:** CSV `token_data_export` `hellion_winded` / `winded_p1` / `winded_p2`; `hel_howling_end` `m_AllConditionIds,performer_has_3_winded`; `hel_bloodlust` `performer_has_1_or_more_winded` and `remove_all_winded`; `hel_adrenaline_rush` `remove_all_winded`; winded-tag attacks `hel_wicked_hack`, `hel_iron_swan`, `hel_bleed_out`, `hel_breakthrough`, `hel_if_it_bleeds`, `hel_barbaric_yawp`. Wiki [Hellion (DD2)](https://darkestdungeon.wiki.gg/wiki/Hellion_(Darkest_Dungeon_II)): Howling End is the burst; Toe to Toe / Adrenaline clear Winded.
- **Rules encoded:**
  - At 0–2 Winded, a winded-tag attack is the generate click (small pay). Do not Adrenaline/Bloodlust while healthy and under 3.
  - At 3 Winded, Howling End is the spend. Do not stack further unless the click kills. Bloodlust is the dump if Howling End is not legal.
  - Toe to Toe walks this hero toward rank 0 unless another frontliner already owns rank 0 (see rank occupancy). Harvest hunger still pays the walk.
- **Not encoded:** Ravager vs Carcass; “Howling End turn 1 always”; shrine unlock order.

## Blind / Ruin (Leper)

- **Sources:** CSV `lep_chop` / `lep_hew` `performer_effects,leper_self_blind_chance` and `token_ignores,til_combo_ignore_blind`; `lep_reflection` `remove_all_blind`; `lep_ruin` `m_AllConditionIds,performer_no_lep_ruin_counter` and `add_2_lep_ruin_counter`. Wiki [Leper (DD2)](https://darkestdungeon.wiki.gg/wiki/Leper_(Darkest_Dungeon_II)) Strategy is a stub; Combo-ignore-Blind is the CSV fact.
- **Rules encoded:**
  - Do not Chop/Hew into Blind unless the target has Combo.
  - Reflection clears Blind. Do not Reflection when not Blind.
  - A Blind attack that scores below 0 loses to any better legal click (Reflection / Withstand).
  - Ruin is friendly Support — pay the charge while `ruin_ready` is down; do not recast while ready.
  - When the Leper is urgent (DoT lethal / ≤15%), Solemnity on self is paid; heals aimed at healthier allies are soft-docked.
- **Not encoded:** Tempest vs Poet; Command as a partner (that is MAA synergy, not this loop).

## Pain (Flagellant)

- **Sources:** CSV `flagellant` `m_Tags` include `no_dd_weak`; tokens `pain_heal` / `flagellant_pain_heal`, `more_more` / `flagellant_more_more`; `flg_more_more` `add_2_taunt_nr`. Wiki [Flagellant (DD2)](https://darkestdungeon.wiki.gg/wiki/Flagellant_(Darkest_Dungeon_II)): More MORE! is the tank click.
- **Rules encoded:**
  - A Flagellant (or anyone with Pain / More MORE!) is not a crisis-heal target while above ~25% HP — below that floor, or when the next DoT tick would kill, medicine / Redeem fire as crisis heals (do not wait for Death's Door).
  - Crisis heals are not skipped to finish the last enemy, peel, or clear a corpse when the target is urgent (Death's Door, dies-to-DoT, or ≤~15% HP). A real heal that outscores the best attack by ≥40 still wins.
  - Items (bandage) do not burn the once-per-round skill-heal gate; Rest / Solemnity / BM can still fire the same round.
  - More MORE! is paid as self-taunt while two or more enemies live.
- **Not encoded:** Exanimate / Maniac / Scourge path loops; Deathless as a named spend (heal scoring already fires at Death's Door).

## Rank occupancy (party)

- **Sources:** CSV launch ranks: Hellion/Leper/Flagellant/MAA main attacks `launch_ranks,1,2` (game rank 0–1). `hel_toe_to_toe` / `maa_hold_the_line` `move_forward_1`. Harvest Child note already names those two walks.
- **Rules encoded:**
  - Those four prefer rank 0.
  - If a living ally who prefers the front already sits rank 0, do not pay Toe to Toe / Hold the Line / Rampart that steals the tile.
  - Harvest hunger (`hold_the_line` / `toe_to_toe` +80) still wins.
  - Duelist's Advance is docked when it would shove an Acid Rain ally from the rank in front of the Highwayman onto rank 2+ (Acid Rain launch is ranks 0–1 only).
  - Do not rank-walk for a must-kill an ally already hits from their current rank (MAA shoving Dismas off the Pistol rank, then Dismas walking back). Librarian still never swaps the hero who punches him.
  - Reach walks score lower while the party is in crisis so Endure / BM / Rest beat a 180 move.
  - Man-at-Arms Retribution (Taunt + Riposte) is the team's one Riposte open: this hero's token is down, ≥2 enemies live, not over a kill / last-bar finish / Cabin Boy burst (`BurstBeforeEvolve`) / a damaging hit on a living Altar or Focused Fault stalk. Do not open a second Riposte if an ally already has one, unless a living hero is at ≤45% (Taunt should pull those hits). Highwayman Take Aim is not this gate — Duelist's Advance plants Riposte on the attack.
- **Not encoded:** dancing comps (Jester Echoing March) as a full rank script.

## Combo reach (party)

- **Sources:** existing Combo apply in `TokenPrices` (`apply_combo` only if a follow-up spender has not acted). Spend skill `target_ranks` from each spender's equipped CSV. Wiki Combo is consumed by the spender's skill, not by a mark on an unreachable rank.
- **Rules encoded:**
  - Do not pay `apply_combo` on an enemy rank no remaining Combo spender can hit from their current launch rank.
  - If we cannot read a spender's target ranks, fail open (keep paying) so the playtested four still mark.
  - A 0-damage Combo mark does not inherit must-kill / boss focus. Combo pay still applies. A damaging hit on the same actor still beats the mark.
- **Not encoded:** “mark the highest HP target”; Tracking Shot vs Blind Gas preference.

## Extra action (Jester)

- **Sources:** CSV `jes_the_last_laugh` `target_effects,extra_action` plus self `add_1_daze_nr,add_1_weak_nr`. Path Encore skills (`jes_encore_p1`) restore Finale buffs — they are not this loop.
- **Rules encoded:** pay extra action on a living attacker or a Death's Door ally; do not click it on a healthy healer.
- **Not encoded:** Virtuoso encore-tracker restore (`jes_encore_p2`).

## Conviction (Vestal)

- **Sources:** CSV `ves_blessing_of_light` / `ves_blessing_of_fortitude`; `ves_pay_conviction_cost` on Divine Grace, Mantra, Judgement, Mace Bash. `ves_blessing_add_1_conviction` generates stacks from a blessing.
- **Rules encoded:** Blessing is the generate click at 0 stacks; do not recast at 3. Grace/Mantra spend on a crisis ally when Conviction ≥ 1. Judgement/Mace Bash get a small spend pay at ≥ 2.
- **Not encoded:** Seraph / Glimmer path conviction.

## DoT host (party)

- **Sources:** snapshot `BleedDot` / `BlightDot` / `BurnDot`. Bleed skills: If It Bleeds, Bleed Out, Incision, Punish, Harvest, Slice Off, Open Vein (`hwm_open_vein`, Combo doubles via `combo_increase_bleed_dealt_100pct` + `end_combo`). Blight: Noxious, Rain of Sorrows, Acid Rain, Spit. Burn: Firefly, Searing, Dragonfly, Controlled Burn, Judgement, Holy Lance, Zealous Accusation.
- **Rules encoded:** opening a DoT on a clean target is small setup; stacking the same DoT without a kill is docked. Live preview effect IDs supply Bleed/Blight/Burn magnitude (`GetDotMagnitude`). Skill-locked trinket stats (`skill_is_*` + `dot_effect_value_dealt_change`) add on top. Open Vein Combo doubles bleed (`combo_increase_bleed_dealt_100pct`). Pay `amount × land × ticks`, not a flat +5.
- **Not encoded:** DoT duration from the Dot def (ticks is 1.5 while 2+ enemies live). Resist pierce is preview `m_PerformerResistanceIgnoreStatValues` subtracted from target resist.

## Enemy resists

- **Sources:** CSV `sub_stat,resistance,{stun,blight,bleed,burn,debuff,...}`. Live preview `m_TargetResistanceStatValues` and pierce `m_PerformerResistanceIgnoreStatValues`. Not a faction type chart (ignited Fanatic burn 75% vs unignited 40%).
- **Rules encoded:** HP damage already uses the preview. DoT open-host and stun/daze/debuff token pay scale by land chance `(1 - effective resist)`. Land ≤ 35% (mostly bounces) is a soft waste (−12). Land ≤ 5% (resist ≥ ~95%, including immunity like barricade `res_blight` 2.0) is a hard waste (−90) so Noxious / Firefly / Blind / stun do not beat a real swing.
- **Not encoded:** handwritten “Sprawl = no burn” / “Gaunt = blight-weak” tables.

## Self tokens on attacks

- **Sources:** CSV `performer_effects` on attacks (e.g. `hwm_duelists_advance` Riposte). Preview `ApplyPerformer` from `PERFORMER*` groups.
- **Rules encoded:** when the click is an enemy attack, pay Riposte / Block / Dodge / Strength / Taunt / Crit on the performer if they do not already have it. Self-target support still uses `ApplyTarget`.
- **Not encoded:** Take Aim as a named skill (it is not the Retribution open).

## Stun next in order

- **Sources:** `QueryTurnOrder.m_RemainingTurnOrder`. First living enemy in that list is about to act.
- **Rules encoded:** stun/daze on that enemy pays extra (`stun_next`). Land chance still applies.
- **Not encoded:** stun the biggest HP bar regardless of order.

## Blocked hits and Guard

- **Sources:** preview `m_IsBlocked` / `IsBlocked`; `m_GuardingActorGuid`. Guard redirects the attack onto the guardian.
- **Rules encoded:** a blocked hit with no real damage is a waste unless it strips Block (`peel_block`). Kill overlay uses the guardian's HP, not the click target's. `m_GuardingActorGuid` is the protected actor when you click them (same guid as the click); the guardian is the other `HitGuids` entry. A redirected hit does not count as damaging the commander / must-kill (no commander focus pay, does not arm the deferred-add veto). Living 0 HP (Death Armor) with connecting damage is a kill; do not leave that chip for an ally.
- **Not encoded:** waiting out 3 Block+ without a strip.

## Pull / knockback reach

- **Sources:** CSV `move_pull_1` / `move_knockback_1` (`occ_daemons_pull`, `abm_manacles`, `maa_rampart`, `abm_cuff`, `lep_stagger`). Move resist in preview `resistance,move`.
- **Rules encoded:** pay the shuffle only when the current enemy rank is not hittable by any attacker and the destination rank is. Land ≤ 35% is a waste.
- **Not encoded:** multi-step slides; always pull the backline.

## Corpse clog

- **Sources:** CSV pouch_of_lye `target_is_corpse_hidden` / `clear_corpse`; hero skills with `target_team_effects,clear_corpse` (`lep_purge`, also Occultist / GR / Flagellant clears). A corpse in a lower rank than every living enemy occupies the front.
- **Rules encoded:** Clear that corpse (`corpse_reach`) with Lye or a clear skill (Purge), especially when this hero has no damaging hit on the last enemy or the must-kill. Death's Door heal still goes first. A 0-damage Combo mark is not a skill to walk for. Rank walk still fires when this hero cannot damage the must-kill, except when an ally already hits that rank from their current tile (do not shove them off it). Librarian still never swaps the hero who already punches him. Multi-hit (Flashing Daggers `m_IsMultiHit`) scores the sum of living HP it hits, not the click target alone and not corpse HP. A cone that `HitGuids` a corpse loses to a click of the same skill that hits more living enemies, or (if it only tags one living) to a clean living single-target. Two living hits still beat Pick even if a corpse is also in the cone.
- **Not encoded:** auto-slide after clear (game-dependent).

## Hearthlight / Firestarter (Runaway)

- **Sources:** CSV `run_hearthlight` `target_team_effects,remove_all_stealth`; `run_firestarter` ally Burn buff.
- **Rules encoded:** Hearthlight pays if any enemy is stealthed, otherwise it loses to a real Firefly. Firestarter pays on an attacking ally.
- **Not encoded:** Controlled Burn token stacking.

## Beast form (Abomination)

- **Sources:** CSV token `beast_mode`; human skills `abm_absolution`, `abm_manacles`, `abm_cuff`, `abm_spit`; beast skills `abm_rake`, `abm_maul`, `abm_rage`, `abm_beasts_bile`, `abm_howl`. Transform/Revert already in kit-safety.
- **Rules encoded:** do not click the other form's skills if a leak is legal.
- **Not encoded:** path-specific transform riders.

