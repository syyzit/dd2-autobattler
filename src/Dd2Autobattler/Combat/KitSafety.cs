using System;

namespace Dd2Autobattler.Combat
{
    // Unique-kit loops the generic scorer cannot see. CSV token ids / skill
    // tags; not a per-hero AI. Setup skills are m_IsFriendly (Support) and
    // lose to any attack unless we pay them here.
    internal sealed class KitContext
    {
        public SkillKind Kind;
        public bool EnemyTarget;
        public PreviewScore Preview;
        public TargetInfo Target;
        public TargetInfo Performer;
        public int LivingEnemies;
        public bool PartyDoor;
        public bool FrontOccupied;
        public bool AnyEnemyStealth;
        public bool TargetAttacks;
        public bool TargetHeals;
        public PartyKit Party;
    }

    internal static class KitSafety
    {
        internal static float Score(string skillId, KitContext ctx, out string reason)
        {
            reason = null;
            if (string.IsNullOrEmpty(skillId) || ctx == null || ctx.Performer == null)
                return 0f;

            var preview = ctx.Preview;
            var target = ctx.Target;
            var performer = ctx.Performer;
            var kills = preview != null && preview.Kills;
            var hpPct = performer.HpPct;
            var total = 0f;
            var bestAbs = 0f;
            var bestWhy = (string)null;

            void Add(float part, string why)
            {
                if (part == 0f)
                    return;
                total += part;
                if (Math.Abs(part) >= 12f && Math.Abs(part) >= bestAbs)
                {
                    bestAbs = Math.Abs(part);
                    bestWhy = why;
                }
            }

            var applyWinded = preview != null && TokenPrices.HasId(preview.ApplyPerformer, "winded");
            var clearWinded = preview != null && (TokenPrices.HasId(preview.RemovePerformer, "winded")
                || TokenPrices.HasId(preview.ConsumePerformer, "winded"));
            var winded = WindedDelta(skillId, performer.Winded, kills, hpPct, applyWinded, clearWinded);
            Add(winded, IdHas(skillId, "howling_end") ? "howling_end" : "kit_winded");
            Add(BlindDelta(skillId, ctx.Kind, ctx.EnemyTarget, performer.Blind, target != null && target.Combo, kills),
                IdHas(skillId, "reflection") ? "clear_blind" : "kit_blind");
            Add(RuinDelta(skillId, performer.RuinReady, ctx.LivingEnemies), "ruin_charge");
            Add(TauntSetupDelta(skillId, ctx.Kind, ctx.EnemyTarget, ctx.LivingEnemies), "kit_taunt");
            Add(PartySynergy.FrontWalkDelta(skillId, performer.Rank, ctx.FrontOccupied),
                ctx.FrontOccupied ? "rank_occupied" : "rank_walk");
            Add(PartySynergy.MoveDelta(skillId, target != null ? target.Rank : -1, ctx.EnemyTarget,
                    target != null && target.Corpse, ctx.Party, preview != null ? preview.Land("move") : 1f),
                IdHas(skillId, "pull") || IdHas(skillId, "manacles") ? "pull_reach" : "knock_reach");
            Add(FinaleDelta(skillId, kills, ctx.LivingEnemies, target != null ? target.Hp : 0f), "wasted_finale");
            Add(WyrdDelta(skillId, target, ctx.EnemyTarget), "wyrd_healthy");
            Add(ChaoticOfferingDelta(skillId, hpPct, performer.DeathsDoor), "chaotic_offering");
            Add(StanceDelta(skillId, ctx.Kind, performer.AggressiveStance, performer.DefensiveStance), "stance_keep");
            Add(TransformDelta(skillId, performer.BeastMode, hpPct, performer.DeathsDoor, ctx.PartyDoor, ctx.LivingEnemies),
                IdHas(skillId, "revert") ? "stay_beast" : "transform_beast");
            Add(BeastFormDelta(skillId, performer.BeastMode), "form_mismatch");
            Add(ConvictionDelta(skillId, performer.Conviction, target, ctx.EnemyTarget), "conviction");
            Add(ExtraActionDelta(skillId, target, ctx.EnemyTarget, ctx.TargetAttacks, ctx.TargetHeals), "extra_action");
            Add(DotHostDelta(skillId, target, ctx.EnemyTarget, kills, ctx.LivingEnemies, preview), "dot_host");
            Add(HearthlightDelta(skillId, target, ctx.AnyEnemyStealth), "hearthlight");
            Add(FirestarterDelta(skillId, ctx.EnemyTarget, ctx.TargetAttacks), "firestarter");
            Add(HealthlessMarkDelta(skillId, target, ctx.EnemyTarget, preview), "taproot_mark");

            reason = bestWhy;
            return total;
        }

        // Tracking Shot / Blinding Gas on healthless Taproot: Combo cannot be
        // spent, preview damage is always 0. Wiki tap is "when Hit" — use a
        // real swing if one is legal.
        internal static float HealthlessMarkDelta(string skillId, TargetInfo target, bool enemyTarget, PreviewScore preview)
        {
            if (!enemyTarget || target == null || (!target.Healthless
                && (target.ClassId == null || target.ClassId.IndexOf("taproot", StringComparison.OrdinalIgnoreCase) < 0)))
                return 0f;
            if (TurnDecider.IsComboOnlyTap(skillId, preview))
                return -80f;
            return 0f;
        }

        internal static bool WantsToStayLow(TargetInfo target)
        {
            if (target == null || target.DeathsDoor || target.Corpse)
                return false;
            if (target.Pain || target.MoreMore)
                return true;
            return IsFlagellant(target.ClassId);
        }

        // Howling End requires 3x hellion_winded. Other winded-tag attacks stack it.
        // Adrenaline / Bloodlust clear. Do not dump stacks while healthy and under 3.
        internal static float WindedDelta(string skillId, int winded, bool kills, float performerHpPct, bool applySelf = false, bool clearSelf = false)
        {
            if (IdHas(skillId, "howling_end"))
                return winded >= 3 ? 28f : 0f;
            if (ClearsWinded(skillId) || clearSelf)
            {
                if (winded <= 0)
                    return 0f;
                if (performerHpPct <= 0.40f)
                    return 8f;
                if (winded < 3)
                    return -10f;
                return 16f;
            }
            if (!AppliesWinded(skillId) && !applySelf)
                return 0f;
            if (winded >= 3 && !kills)
                return -32f;
            if (winded < 3)
                return 6f;
            return 0f;
        }

        // Chop/Hew ignore Blind when the target has Combo (til_combo_ignore_blind).
        // Reflection strips Blind. Other swings into Blind miss.
        internal static float BlindDelta(string skillId, SkillKind kind, bool enemyTarget, bool blind, bool targetCombo, bool kills)
        {
            if (IdHas(skillId, "reflection"))
                return blind ? 32f : -8f;
            if (!blind)
                return 0f;
            if (kind != SkillKind.Attack || !enemyTarget)
                return 0f;
            if (targetCombo && (IdHas(skillId, "chop") || IdHas(skillId, "hew")))
                return 0f;
            if (kills)
                return -12f;
            return -36f;
        }

        // lep_ruin is friendly Support. Without a bonus Leper never charges it.
        internal static float RuinDelta(string skillId, bool ruinReady, int livingEnemies)
        {
            if (!IdHas(skillId, "lep_ruin") || IdHas(skillId, "counter"))
                return 0f;
            if (ruinReady)
                return -20f;
            return livingEnemies >= 2 ? 24f : 10f;
        }

        // More MORE!, Withstand, Intimidate, Bulwark, Hold the Line: self-taunt
        // is the tank click. Support/0-damage otherwise loses to any swing.
        internal static float TauntSetupDelta(string skillId, SkillKind kind, bool enemyTarget, int livingEnemies)
        {
            if (livingEnemies < 2)
                return 0f;
            if (!IsSelfTaunt(skillId))
                return 0f;
            if (kind == SkillKind.Attack && enemyTarget)
                return 8f;
            return 22f;
        }

        // jes_finale: CD 3, self Vulnerable + Daze. Not a chip cleaner.
        internal static float FinaleDelta(string skillId, bool kills, int livingEnemies, float targetHp)
        {
            if (!IdHas(skillId, "finale") || IdHas(skillId, "buff") || IdHas(skillId, "encore"))
                return 0f;
            if (kills)
                return 12f;
            if (livingEnemies > 1 && targetHp <= 12f)
                return -55f;
            if (livingEnemies > 1)
                return -18f;
            return 0f;
        }

        // occ_wyrd_reconstruction: heal + bleed rider. Existing heal scoring
        // already docks a healthy target; extra so it never beats a real attack.
        internal static float WyrdDelta(string skillId, TargetInfo target, bool enemyTarget)
        {
            if (!IdHas(skillId, "wyrd") || enemyTarget || target == null || target.Corpse)
                return 0f;
            if (target.DeathsDoor)
                return 0f;
            if (target.HpPct > 0.55f)
                return -30f;
            return 0f;
        }

        // occ_chaotic_offering: 15% self damage, health_over_15pct.
        internal static float ChaoticOfferingDelta(string skillId, float performerHpPct, bool deathsDoor)
        {
            if (!IdHas(skillId, "chaotic_offering"))
                return 0f;
            if (deathsDoor || performerHpPct <= 0.40f)
                return -40f;
            return 8f;
        }

        // Meditation/Preparation recast the stance you already have.
        // Fleche/Disengage are attacks that also set stance — leave them.
        internal static float StanceDelta(string skillId, SkillKind kind, bool aggressive, bool defensive)
        {
            if (kind == SkillKind.Attack)
                return 0f;
            var setsAgg = IdHas(skillId, "preparation") || IdHas(skillId, "fleche");
            var setsDef = IdHas(skillId, "meditation") || IdHas(skillId, "disengage");
            if (setsAgg && aggressive)
                return -22f;
            if (setsDef && defensive)
                return -22f;
            if ((setsAgg || setsDef) && !aggressive && !defensive)
                return 20f;
            return 0f;
        }

        // jes_the_last_laugh: extra_action on an ally (Wanderer Encore). Self Daze+Weak.
        internal static float ExtraActionDelta(string skillId, TargetInfo target, bool enemyTarget, bool targetAttacks, bool targetHeals)
        {
            if (!IdHas(skillId, "the_last_laugh"))
                return 0f;
            if (enemyTarget || target == null || target.Corpse)
                return -20f;
            if (target.DeathsDoor)
                return 24f;
            if (targetAttacks)
                return 32f;
            if (targetHeals && target.HpPct > 0.55f)
                return -16f;
            return 6f;
        }

        // Blessing of Light/Fortitude generates Conviction on the blessed hero.
        // Divine Grace / Mantra / Judgement / Mace Bash spend it (ves_pay_conviction_cost).
        internal static float ConvictionDelta(string skillId, int conviction, TargetInfo target, bool enemyTarget)
        {
            if (IdHas(skillId, "blessing_of_light") || IdHas(skillId, "blessing_of_fortitude"))
            {
                if (conviction >= 3)
                    return -12f;
                if (conviction <= 0)
                    return 18f;
                return 6f;
            }
            if (IdHas(skillId, "divine_grace") || IdHas(skillId, "mantra"))
            {
                if (conviction < 1 || enemyTarget || target == null || target.Corpse)
                    return 0f;
                if (target.DeathsDoor || target.HpPct <= 0.35f)
                    return 8f * Math.Min(conviction, 3);
                return 0f;
            }
            if ((IdHas(skillId, "judgement") || IdHas(skillId, "mace_bash")) && conviction >= 2)
                return 8f;
            return 0f;
        }

        // Opening a DoT on a clean target is setup. Stacking the same DoT
        // when it does not kill is a worse click than a new host.
        internal static float DotHostDelta(string skillId, TargetInfo target, bool enemyTarget, bool kills, int livingEnemies, PreviewScore preview = null)
        {
            if (!enemyTarget || target == null || target.Corpse || kills)
                return 0f;
            var score = 0f;
            score += DotKindDelta(preview != null ? preview.ApplyBleed : 0f, target.BleedDot,
                preview != null ? preview.Land("bleed") : 1f, livingEnemies, AppliesBleed(skillId));
            score += DotKindDelta(preview != null ? preview.ApplyBlight : 0f, target.BlightDot,
                preview != null ? preview.Land("blight") : 1f, livingEnemies, AppliesBlight(skillId));
            score += DotKindDelta(preview != null ? preview.ApplyBurn : 0f, target.BurnDot,
                preview != null ? preview.Land("burn") : 1f, livingEnemies, AppliesBurn(skillId));
            return score;
        }

        internal static float DotKindDelta(float amount, float alreadyOn, float land, int livingEnemies, bool named)
        {
            if (amount >= 1f)
                return PreviewScore.DotApplyPay(amount, alreadyOn, land, livingEnemies);
            if (!named)
                return 0f;
            if (alreadyOn > 1f)
                return -10f;
            return livingEnemies >= 2 ? PreviewScore.DotOpenPay(land) : 0f;
        }

        // run_hearthlight: team stealth strip. Pay it if anyone is stealthed.
        internal static float HearthlightDelta(string skillId, TargetInfo target, bool anyEnemyStealth)
        {
            if (!IdHas(skillId, "hearthlight"))
                return 0f;
            if (anyEnemyStealth || (target != null && target.Stealth))
                return 22f;
            return -8f;
        }

        // run_firestarter: ally Burn buff. Pay it on an attacker.
        internal static float FirestarterDelta(string skillId, bool enemyTarget, bool targetAttacks)
        {
            if (!IdHas(skillId, "firestarter"))
                return 0f;
            if (enemyTarget)
                return -20f;
            return targetAttacks ? 16f : 4f;
        }

        // Beast-mode skills vs human skills. Illegal across forms in CSV;
        // if a click leaks, do not take it.
        internal static float BeastFormDelta(string skillId, bool beast)
        {
            if (IdHas(skillId, "absolution") || IdHas(skillId, "manacles") || IdHas(skillId, "cuff")
                || IdHas(skillId, "spit"))
                return beast ? -30f : 0f;
            if (IdHas(skillId, "rake") || IdHas(skillId, "maul") || IdHas(skillId, "abm_rage")
                || IdHas(skillId, "beasts_bile") || IdHas(skillId, "abm_howl"))
                return beast ? 0f : -30f;
            return 0f;
        }

        // abm_transform is friendly Support (party stress). Never wins vs Slam
        // without a bonus. Revert heals — only from beast when hurt.
        internal static float TransformDelta(string skillId, bool beast, float performerHpPct, bool deathsDoor, bool partyDoor, int livingEnemies)
        {
            if (IdHas(skillId, "transform") && !IdHas(skillId, "revert"))
            {
                if (beast)
                    return -40f;
                if (partyDoor)
                    return -20f;
                return livingEnemies >= 2 ? 55f : 18f;
            }
            if (!IdHas(skillId, "revert"))
                return 0f;
            if (!beast)
                return -20f;
            if (deathsDoor || performerHpPct <= 0.35f)
                return 36f;
            if (livingEnemies >= 1 && performerHpPct > 0.50f)
                return -50f;
            return 0f;
        }

        internal static bool AppliesWinded(string skillId)
        {
            return IdHas(skillId, "wicked_hack")
                   || IdHas(skillId, "iron_swan")
                   || IdHas(skillId, "bleed_out")
                   || IdHas(skillId, "breakthrough")
                   || IdHas(skillId, "if_it_bleeds")
                   || IdHas(skillId, "barbaric_yawp");
        }

        internal static bool ClearsWinded(string skillId)
        {
            return IdHas(skillId, "adrenaline_rush") || IdHas(skillId, "bloodlust");
        }

        internal static bool IsSelfTaunt(string skillId)
        {
            return IdHas(skillId, "more_more")
                   || IdHas(skillId, "withstand")
                   || IdHas(skillId, "intimidate")
                   || IdHas(skillId, "bulwark")
                   || IdHas(skillId, "hold_the_line")
                   || IdHas(skillId, "retribution")
                   || IdHas(skillId, "toe_to_toe");
        }

        internal static bool AppliesBleed(string skillId)
        {
            return IdHas(skillId, "if_it_bleeds")
                   || IdHas(skillId, "bleed_out")
                   || IdHas(skillId, "incision")
                   || IdHas(skillId, "punish")
                   || IdHas(skillId, "jes_harvest")
                   || IdHas(skillId, "slice_off")
                   || IdHas(skillId, "open_vein");
        }

        internal static bool AppliesBlight(string skillId)
        {
            return IdHas(skillId, "noxious")
                   || IdHas(skillId, "rain_of_sorrows")
                   || IdHas(skillId, "acid_rain")
                   || IdHas(skillId, "abm_spit");
        }

        internal static bool AppliesBurn(string skillId)
        {
            return IdHas(skillId, "firefly")
                   || IdHas(skillId, "searing")
                   || IdHas(skillId, "dragonfly")
                   || IdHas(skillId, "controlled_burn")
                   || IdHas(skillId, "judgement")
                   || IdHas(skillId, "holy_lance")
                   || IdHas(skillId, "zealous");
        }

        internal static bool IsFlagellant(string classId)
        {
            return IdHas(classId, "flagellant");
        }

        internal static bool IdHas(string id, string key)
        {
            return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(key)
                   && id.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
