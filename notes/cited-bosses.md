# Cited boss notes

Hero resource loops (Winded, Blind/Ruin, Pain, Combo reach) live in `cited-classes.md`. Same stack.

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
  - Taproot is healthless: preview damage is always 0. A connecting attack still counts as the wiki "when Hit" tap. Do not walk to reach Taproot — if this hero cannot tap it, hit the General.
  - Tracking Shot / Blinding Gas on Taproot is a wasted Combo mark. Never pick it; use a connecting swing if one is legal, otherwise hit the General.
  - Healthless fixtures do not count as killable enemies. A last-General kill (or a slap on Death Armor at 0 HP) is allowed even if Taproot is still up and someone is on Death's Door.
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
- **Rules encoded:** Bishop `must_kill` first; Drummer `commander` next. Knight / foot / arbalist deferred while either is a connecting legal target. Guard redirect onto a foot is not a Drummer hit. A 0-damage Combo mark on a deferred add is skipped while the Drummer / Bishop is up. Controller flags share one predicate with add-marking so a mash Drummer is not tagged add.

## Harvest Child (Foetor lair)

- **Sources:** [wiki Strategy](https://darkestdungeon.wiki.gg/wiki/Harvest_Child#Strategy_&_Advice). CSV/classes: `plague_eater_harvest_table` (Child), `*_fetid_meat`, `*_putrid_meat`. Hunger token `harvest_hunger`; rank 1 is forced `harvest_hunger` / Feed the Hunger (-15% max HP, unresistable).
- **Rules encoded:**
  - Focus the table. Both meats are deferred while the table is alive. Do not finish a meat if the table is a legal attack.
  - Rank 1 with `harvest_hunger` is forced Feed the Hunger (CSV `harvest_hunger`, `target_is_meat`). That click is not skipped as a deferred meat punch or a crisis-add punch — the game has no other skill.
  - If another hero has Harvest Hunger and this hero does not, prefer MAA Hold the Line / Hellion Toe to Toe (wiki: take rank 1 and immobilize so the hungry hero is not pulled into the eat). Those skills are not vetoed just because they hit a meat.
  - If this hero is hungry, those same forward-moves are penalized (they walk you into rank 1).
- **Not encoded:** inn prep (Apples and Cheese, antivenom). Immobilize on the meats themselves (Leper Bash / net) is not wired.

## Relationship curses

- **Sources:** CSV `skill_modifier_data_export` - negative relationships set `m_IsForceEquip` on the cursed skill (`hateful_a`, `suspicious_a`, `envious_a`, `resentful_*`). Using it applies the rider to the partner (Vulnerable, Taunt, stress, Blind/Weak).
- **Rules encoded:** if a cursed attack/support is legal and a non-cursed attack is also legal, the cursed skill is penalized (-40). Crisis heals are never penalized. If the cursed skill is the only hit on the focus target, it still wins.

## Healer rank

- **Sources:** Battlefield Medicine `launch_ranks` 3,4; `m_Limit` 3. Hero `*_move` is adjacent swap (`m_IsMoveToTarget`, relative -1/+1).
- **Rules encoded:** if an ally is in crisis (Death's Door or <=35%) and no skill heal is legal, allow a move that lands the healer on a launch rank of an equipped heal that still has uses. Do not un-veto every swap. An enemy attack with a self-heal rider (Crush on Combo) is not a skill heal — do not walk for it.
- Cleanse items that also heal (antivenom, bandage) are spent on Death's Door / <=30% instead of being scored as a wasted cleanse.
- If the skill heal is spent (`m_Limit`) and someone is on Death's Door or <=30%, food/bandage/antivenom that still heal are forced. A last-enemy kill does not skip a Death's Door heal. Support (Ounce of Prevention) is penalized while the party is in crisis.
- Last living enemy behind corpses, or a must-kill this hero cannot damage (Librarian after Categorize): if no damaging attack is legal on that target, walk onto a launch rank of an equipped attack that can hit that rank. 0-damage Combo marks do not count as a hit and are not a skill to walk for. `pass_heal` is Rest, not a skill heal — do not walk for it. Do not un-veto every swap. Skip that walk when an ally already hits the tile from their current rank (MAA/Dismas ping-pong on Altar). Still never swap the hero who already punches the Librarian.
- Combat items are classified from CSV conditions/tags, not a name list: `target_is_corpse_hidden` (Pouch of Lye), `target_has_*_dot` (DoT cleanse), `target_is_diseased` (Single Leech), `target_has_blind` (Rag), `stress_heal`, `heal`. Enemy items stay attacks (witchbane). Grenades that click a corpse still skip. Hero clear skills (`lep_purge` `clear_corpse`) use the same `corpse_reach` boost when a front corpse blocks living targets. Buffs (powders, war horn, The Blood) stay on the token/buff scorer — do not dump them.

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
  - A 0-damage Combo mark is not a hit. If this hero cannot damage him from this rank, walk onto a launch rank of a damaging attack (Pistol, Chop, Judgement). Do not restack Combo. If a damaging attack on him is already legal, take it over Tracking Shot.
  - One reach walk per round for the party. Do not swap the ally who already reaches him (Categorize ping-pong). Out of reach after that: support/pass, not another walk.
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
  - Do not LeaveChip a stalk. A 1 HP Cluster still Gazes / applies Seen.
  - Non-killing AoE on stalks (no DoT) loses to a single-target into one lane. Wiki AoE is a kill or a DoT finish, not a double-split.
  - Do not open Retribution / Riposte setup over a damaging or DoT hit on a living stalk (same utility gate as Altar).
  - Blind / Combo-only taps lose to a blight/bleed/burn that still tags a living stalk, including when the cone HitGuids corpses. A corpse-targeted DoT cone is the finish, not skip_corpse.
  - Phase 2 (`boss_eyes`): the mass is `must_kill`. Weak on the mass is extra (wiki: Weak/Block blunt Limerence).
  - Heal/guard the hero with `eyes_focus`.
  - Do not stack a third positive token onto a hero who is not Seen while the mass is up (wiki: Suppress on ≥3 positives).
- **Not encoded:** inn prep, outspeed-then-Taunt plan, Dodge-to-avoid-Seen plan. Those are inn/speed, not a targeting rule.

## Leviathan (Shroud lair)

- **Sources:** [wiki Strategy](https://darkestdungeon.wiki.gg/wiki/Leviathan#Strategy). CSV: body `coastal_boss_leviathan` (155 HP, size 2, summons Hand via Deep Rising / `leviathan_summon_hand`); Hand `coastal_boss_leviathan_hand` (24 HP, size 2, 2 turns, tags `boss_hand`). Undertow captures a Call of the Deep mark (per-turn HP drain, per-round stress) until the Hand dies; recast at round end after death.
- **Rules encoded:**
  - While the Hand is up and not dying to DoT, it is `must_kill`. The body is deferred. Wiki: "the hand is by far the most important target"; "only once the hand is dead (or dying from DoT) should the heroes focus on damaging the Leviathan itself." Recast next round is expected — kill it again.
  - If the Hand is dying to DoT, defer it and hit the body.
  - Body-only (Hand not out yet, or already dead this round): no extra rule; default boss scoring.
  - Undertow captures a `call_of_the_deep` mark in hero ranks 1-2 only (CSV `target_has_call_of_the_deep`; wiki: ignores Taunt / Dodge / Blind / Immobilize). If this hero is marked in ranks 1-2 and cannot kill the Hand this click, walk toward ranks 3-4 so Undertow has no legal target.
- **Not encoded:** Guard / Move RES bait; Ceremonial Drums; DoT-RES piercing / blight-burn skill picks. Wiki: do not Taunt-bait Undertow (it ignores Taunt).

## Cultists (Oblivion's Ingress / Rampart)

- **Sources:** [wiki Cultists Strategy](https://darkestdungeon.wiki.gg/wiki/Cultists_(Darkest_Dungeon_II)#Strategy); [wiki Exemplar Strategy](https://darkestdungeon.wiki.gg/wiki/Exemplar#Strategy_and_advice). CSV: `cultist_deacon`, `cultist_cardinal`, `cultist_exemplar`, `cultist_altar`, `cultist_herald`, `cultist_cherub`, `cultist_evangelist`. Worship token cap 2 enables Exultation / the minion **Worship** heal.
- **Rules encoded:**
  - Deacon or Cardinal on the board: while Altar is alive it is the exclusive `must_kill` (Cherub / Evangelist deferred; Herald stays legal for splash). Boss deferred. Wiki: "imperative to kill regular Cultist enemies, especially Altars." When Altar is dead, remaining regulars share `must_kill`. `AltarMustKillBias` still pays Altar over other must-kills; each Worship stack on the boss adds `WorshipStackBias`. A 0-damage Combo mark does not inherit that must-kill focus.
  - Exemplar (Act 3+ last-region Rampart): while Exemplar HP% > ~25% (or Worship ≥ 1 with Altar alive), kill Altar first if alive (`must_kill`; denies Pillar → Regen+Worship — Obsession Altar Taunt makes this especially bad), else Herald (`must_kill`). Exemplar is deferred in those cases; Cherub / Evangelist stay deferred. Herald stays legal (not deferred) while Altar is the must-kill so splash can still chip. When Exemplar HP% ≤ ~25% and Altar is gone (or Worship is 0), Exemplar is `must_kill` and soft adds are deferred; Herald remains legal but not forced. Worship stacks on Exemplar also escalate Altar/Herald focus pay. Skip a deferred punch only when this hero has a damaging must-kill click (HP, or a Taproot tap). Blind Gas / Tracking Shot / any Combo-only tap is not that click — Pillar is Exemplar's forced skill, Blind on Altar does not stop it. If this hero cannot damage Altar, walk onto a launch rank; if an ally already reaches it, a real swing on the deferred target is allowed. On Altar, a bigger HP swing beats a higher-score DoT open unless Altar already dies to DoT.
  - Holy Water / Combo-strip on a hero who has Combo: spend it — PickAction hard-gate (same utility rule as Implication Blind: not over a kill / last-bar / party crisis). Wiki: The Fall on Combo is how Exemplar gains Worship for Exultation. Same strip gate applies on Ravenous Reach p1 (Setback).
  - The Fall (CSV `exemplar_the_fall`): Combo-gated, `target_ranks` 1-3. Taunt on a rank-4 hero skips it (wiki: forces Prelude / Rapturous Beauty) — hard-gated like Combo-strip. If Combo is live in ranks 1-3 and this hero cannot strip it, walk onto rank 4 when an equipped Taunt skill launches from there (not Hold the Line / Toe to Toe — those walk you forward). Do not Fall-walk while Altar is the must-kill (Pillar is worse than The Fall, and the walk spends the party's one reach-walk). Retribution does not open over a damaging Altar hit.
  - Defender / Guard on a Combo ally when this hero does not also have Combo: The Fall hits the guarder; Worship only if that guarder has Combo — also hard-gated.
- **Not encoded:** Shred of Decency as a named item (it already classifies as a strip if the CSV says so); inn blight resist. Kingdoms Tundra Exemplar.

## Ravenous Reach (Ambition / Confession 4)

- **Sources:** [wiki Strategy](https://darkestdungeon.wiki.gg/wiki/Ravenous_Reach#Strategy). CSV: `boss_arms_phase1` / `_phase2` / `_phase3`. p1 Ideation Block+ x3 and Setback (Combo: ignore Dodge, +100% DMG, Stun). p2 Dodge x2 and Teardown. p3 Riposte x2.
- **Rules encoded:**
  - The arms are `must_kill` (only target; marks walk-for-reach).
  - p1: Combo-strip on heroes (Setback) — hard-gated like Exemplar strip (not over a kill / last-bar / party crisis).
  - p2 Dodge x2 / p3 Riposte x2: a legal strip (Tracking Shot, Highway Robbery, Bellow, Magnesium Rain) beats a non-kill attack. Last-enemy Riposte no longer waives the peel — the arms *are* the last enemy.
- **Not encoded:** inn bleed-RES / Fate's Foreteller plan; Sergeant immobility trophy; hero-path skill picks; Bastard's Beacon extra riders. Bleed cleanse on allies is the existing DoT-item policy.

## Body of Work (Cowardice / Confession 5)

- **Sources:** [wiki Body of Work](https://darkestdungeon.wiki.gg/wiki/Body_of_Work) phase Strategy sections. CSV: p1 `boss_body_phase1` (Gut), p2 `boss_body_phase2` (Gaze), p3 `boss_body_phase3` (God 999 HP); Proclaimers `boss_body_cherub`; Spectres `boss_body_failure_*`. Contempt token `torso_target`; Gastric Juice `gastric_juice`. Face Your Failure after both Proclaimers die; Spectre kill unlocks the 200-damage confession attack.
- **Rules encoded:**
  - p1/p2: the body is `must_kill`.
  - p2: do not stack a 4th positive token (wiki: Covetous Glance steals all at 4+). Guard the hero with `torso_target` (Haymaker redirects onto the tank; wiki: Guard, not Taunt — Haymaker ignores Taunt/Dodge/Stealth/Blind). Heal the mark if Guard is not legal. Weak or Block on the Gaze blunts Haymaker.
  - p3: Proclaimers `must_kill`, God deferred. Then Spectre `must_kill`, God deferred. Then the God. Wiki: defeating both Proclaimers unlocks Face Your Failure; the Spectre pay-off is 200 damage (x4 = 800 of the 999).
  - p3: do not pile extra Strength/Block/Dodge (wiki: Strange Axis inverts positives).
- **Not encoded:** inn blight-RES for Catabolize; hero-specific 200-damage skill; Bastard's Beacon. Blight cleanse on Gastric Juice is the existing DoT-item policy.

## Implication (Pillager cannon)

- **Sources:** [wiki Implication](https://darkestdungeon.wiki.gg/wiki/Implication) (Load Shot then BOOOOOOOM! / MISFIRE; often misses). CSV `shared_pillager_artillery`: `pillager_artillery_loading` `performer_effects,count_plus_1,...`; boom `m_AllConditionIds,performer_has_1_count,performer_no_forced_miss`; misfire `performer_has_1_count,performer_has_forced_miss`. Token `count` / `forced_miss`.
- **Rules encoded:**
  - While the cannon has `count` (loaded) and is not Blind and not already `forced_miss`, pay Blind on it (Blinding Gas / any Blind apply) so the shot whiffs. Do not skip a crisis heal or a real kill / last-bar finish for that Blind.
  - Wiki still prefers clearing allies first for damage; this is only the Blind prep while loaded.
- **Not encoded:** pull/push rank games to shrink boom targets; Block+ peel as a must.

## Ordainment

- **Sources:** [wiki Ordainment](https://darkestdungeon.wiki.gg/wiki/Ordainment). HP/DMG (and confession-specific) buffs on trash as the mountain run goes on. Act/lair bosses are never ordained except Bastard's Beacon.
- **Rules encoded:** none. Extra HP and damage already sit in the snapshot and in `QuerySkillPreview`. The wiki has no targeting rule ("kill ordained first" is ours).
- **Not encoded:** confession-specific on-crit token copy, Block→Block+ conversion, invert-on-crit. No combat click follows from those sentences.

## Cabin Boy

- **Sources:** [wiki Cabin Boy](https://darkestdungeon.wiki.gg/wiki/Cabin_Boy). Skills Unstable Incubation / Spawning Ground; Newborn Mutation on the transformed Fisherfolk.
- **Rules encoded:**
  - While any Cabin Boy is alive, set `BurstBeforeEvolve`. Retribution riposte setup and loaded-Implication Blind do not open — Cabin Boys incubate instead of attacking, and a transformed Fisherfolk arrives fully healed with Mutation buffs. Prefer damage.
- **Not encoded:** which Fisherfolk form Spawning Ground prefers by free ranks; disease from Newborn Mutation.

## How to add the next boss

Copy the wiki Strategy paragraph into this file, quote the CSV keys that back the numbers, then encode only those sentences. If a live log looks drunk, write the mismatch here - do not invent a new rule from the log.

Confession mountain bosses are all cited (Denial, Resentment/Seething Sigh, Obsession/Focused Fault, Ambition/Ravenous Reach, Cowardice/Body of Work) plus the Act 3+ Exemplar gate. Roaming (Collector, Death, Shambler, Antiquarian, Warlord) and Kingdoms (Meat Hook, Mother of Threads, Archduke) have no extra targeting note — they play as default boss/support scoring unless a wiki Strategy sentence appears later.
