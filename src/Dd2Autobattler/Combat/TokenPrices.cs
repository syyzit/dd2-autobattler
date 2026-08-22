using System;
using System.Collections.Generic;

namespace Dd2Autobattler.Combat
{
    internal sealed class TokenEval
    {
        public float Score;
        public string Reason;
        public readonly List<string> Apply = new List<string>();
        public readonly List<string> Consume = new List<string>();
        public readonly List<string> Remove = new List<string>();
    }

    internal static class TokenPrices
    {
        private const float SupportCap = 14f;

        public static TokenEval Evaluate(SkillKind kind, bool enemyTarget, PreviewScore preview, TargetInfo target, int livingEnemies, PartyKit party, uint performerGuid, SkillRole role)
        {
            var eval = new TokenEval();
            if (preview == null || !preview.Ok || target == null)
                return eval;

            Copy(preview.ApplyTarget, eval.Apply);
            Copy(preview.ConsumeTarget, eval.Consume);
            Copy(preview.RemoveTarget, eval.Remove);

            var lastEnemy = livingEnemies <= 1 && enemyTarget && !target.Corpse;
            var followUpCombo = party == null || party.FollowUpSpendsCombo(performerGuid);
            var partySpends = party != null && party.PartySpendsCombo;
            var partyAttacks = party == null || party.PartyAttacks;
            var setup = EarlySetupScale(CombatMemory.Round, livingEnemies);
            var bestReason = (string)null;
            var bestPart = 0f;

            void Add(float part, string reason)
            {
                if (part == 0f)
                    return;
                eval.Score += part;
                if (Math.Abs(part) >= Math.Abs(bestPart))
                {
                    bestPart = part;
                    bestReason = reason;
                }
            }

            if (enemyTarget && !target.Corpse)
            {
                var consumesCombo = HasId(eval.Consume, "combo");
                if (consumesCombo && target.Combo)
                {
                    var spend = 14f;
                    if (role != null && party != null && party.BestComboSpend > role.ComboSpendValue + 8f)
                        spend -= 8f;
                    Add(spend, "spend_combo");
                }
                if (HasId(eval.Apply, "combo") && !target.Combo && !lastEnemy && followUpCombo)
                    Add(14f * setup, "apply_combo");
                if (!consumesCombo && target.Combo && !preview.Kills && followUpCombo)
                    Add(-32f, "save_combo");
                if (HasId(eval.Apply, "stun") && !target.Stun)
                    Add(StunPrice(target, lastEnemy, party) * setup, "stun_threat");
                if (HasId(eval.Apply, "daze") && !target.Stun)
                    Add((lastEnemy && (target.Riposte || target.Dodge) ? 10f : 4f) * setup, "stun_threat");
                if (HasId(eval.Apply, "vulnerable") && !target.Vulnerable && partyAttacks)
                    Add((party != null && party.AttackerCount >= 2 ? 10f : 7f) * setup, "apply_token");
                if (HasId(eval.Apply, "weak") && !target.Weak)
                    Add(5f * setup, "apply_token");
                if (HasId(eval.Apply, "blind") && !target.Blind)
                    Add(5f * setup, "apply_token");
                Add(StripEnemy(eval.Remove, target, partySpends), "strip_token");
            }
            else if (!enemyTarget)
            {
                if (HasId(eval.Apply, "strength") && target.StrengthCount < 2)
                {
                    var allyAttacks = party == null || target.Actor == null || party.HeroAttacks(target.Actor.ActorGuid);
                    if (allyAttacks)
                        Add((target.StrengthCount == 0 ? 9f : 3f) * setup, "apply_strength");
                }
                if (HasId(eval.Apply, "block") && target.BlockCount < 3)
                    Add(BlockPrice(target) + (party != null ? party.ProtectBonus(target) * 0.35f : 0f), "apply_block");
                if (HasId(eval.Apply, "dodge") && target.DodgeCount < 2)
                    Add(5f, "apply_token");
                if (HasId(eval.Apply, "riposte") && !target.Riposte)
                    Add(target.Actor != null && target.Actor.TeamPosition <= 1 ? 8f : 4f, "apply_token");
                if (HasId(eval.Apply, "guard"))
                    Add((target.DeathsDoor || target.HpPct <= 0.45f ? 10f : 5f)
                        + (party != null ? party.ProtectBonus(target) * 0.35f : 0f), "apply_token");
                Add(StripAlly(eval.Remove, target), "strip_token");
            }

            if (kind == SkillKind.Support && eval.Score > SupportCap)
                eval.Score = SupportCap;

            if (Math.Abs(bestPart) >= 6f)
                eval.Reason = bestReason;
            return eval;
        }

        internal static bool IsEarlySetup(int round, int livingEnemies)
        {
            return round >= 1 && round <= 2 && livingEnemies >= 3;
        }

        internal static float EarlySetupScale(int round, int livingEnemies)
        {
            return IsEarlySetup(round, livingEnemies) ? 1.5f : 1f;
        }

        public static bool HasId(List<string> ids, string key)
        {
            if (ids == null || string.IsNullOrEmpty(key))
                return false;
            for (var i = 0; i < ids.Count; i++)
            {
                if (!string.IsNullOrEmpty(ids[i]) &&
                    ids[i].IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static float StunPrice(TargetInfo target, bool lastEnemy, PartyKit party)
        {
            var score = 6f;
            if (lastEnemy && (target.Riposte || target.Dodge))
                score = 20f;
            else if (lastEnemy || target.Riposte || target.Hp > 15f)
                score = 14f;
            if (party != null && party.HealerThreatened)
                score += 8f;
            return score;
        }

        private static float BlockPrice(TargetInfo target)
        {
            var score = 5f;
            if (target.DeathsDoor)
                score += 12f;
            else if (target.HpPct <= 0.35f)
                score += 8f;
            else if (target.HpPct <= 0.55f)
                score += 4f;
            if (target.BlockCount >= 2)
                score *= 0.4f;
            return score;
        }

        private static float StripEnemy(List<string> remove, TargetInfo target, bool partySpendsCombo)
        {
            var score = 0f;
            if (HasId(remove, "combo") && target.Combo)
                score -= partySpendsCombo ? 16f : 10f;
            if (HasId(remove, "riposte") && target.Riposte)
                score += 8f;
            if (HasId(remove, "dodge") && target.Dodge)
                score += 6f;
            if (HasId(remove, "stealth") && target.Stealth)
                score += 12f;
            if (HasId(remove, "block") && target.BlockCount > 0)
                score += 6f;
            if (HasId(remove, "strength") && target.StrengthCount > 0)
                score += 6f;
            return score;
        }

        private static float StripAlly(List<string> remove, TargetInfo target)
        {
            var score = 0f;
            if (HasId(remove, "stun") && target.Stun)
                score += 16f;
            if (HasId(remove, "blind") && target.Blind)
                score += 8f;
            if (HasId(remove, "weak") && target.Weak)
                score += 7f;
            if (HasId(remove, "vulnerable") && target.Vulnerable)
                score += 7f;
            return score;
        }

        private static void Copy(List<string> src, List<string> dest)
        {
            if (src == null)
                return;
            for (var i = 0; i < src.Count; i++)
                dest.Add(src[i]);
        }
    }

    internal enum SkillKind
    {
        Attack,
        Heal,
        Support,
        Pass,
        Move
    }
}
