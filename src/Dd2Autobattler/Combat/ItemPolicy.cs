using System;
using System.Collections;
using Assets.Code.Actor;
using Assets.Code.Skill;

namespace Dd2Autobattler.Combat
{
    internal sealed class ItemEval
    {
        public float Score;
        public string Reason;
        public bool Crisis;
        public bool UseNow;
    }

    internal static class ItemPolicy
    {
        public const float UseThreshold = 18f;

        public static bool IsCombatItem(ActorDataSkill def, string skillId, ActorInstance performer)
        {
            if (def != null)
            {
                try
                {
                    if (def.IsItemSkill)
                        return true;
                }
                catch { }
                if (HasTag(def, "combat_item"))
                    return true;
            }

            if (!string.IsNullOrEmpty(skillId))
            {
                try
                {
                    if (performer != null && performer.HasCombatSkillItemWithId(skillId))
                        return true;
                }
                catch { }
            }

            return false;
        }

        public static bool IsFreeAction(ActorDataSkill def)
        {
            try { return def != null && def.m_IsFreeAction; }
            catch { return false; }
        }

        public static int RemainingQty(ActorInstance performer, string skillId)
        {
            if (performer == null || string.IsNullOrEmpty(skillId))
                return 0;
            try { return performer.GetItemCountFromSkillId(skillId); }
            catch { return 0; }
        }

        public static ItemEval Evaluate(string skillId, ActorDataSkill def, SkillKind kind, bool enemyTarget, PreviewScore preview, TargetInfo target, TokenEval tokens, int livingEnemies, int qty)
        {
            var eval = new ItemEval();
            if (target == null)
                return eval;

            // CSV pouch_of_lye: target_is_corpse, clear_corpse, stress_heal on performer.
            // Attack items that happen to click a corpse still take the skip below.
            if (IsCorpseClear(skillId, def) && target.Corpse)
            {
                ScoreCorpseClear(eval, livingEnemies);
                if (qty <= 1)
                    eval.Score -= eval.Crisis ? 2f : 6f;
                else if (qty == 2)
                    eval.Score -= eval.Crisis ? 0f : 2f;
                eval.UseNow = eval.Score >= UseThreshold;
                return eval;
            }

            if (preview == null || !preview.Ok)
                return eval;

            var lastEnemy = livingEnemies <= 1 && enemyTarget && !target.Corpse;
            var fightClosing = livingEnemies <= 1 && !enemyTarget;
            var role = ClassifyItem(skillId, def, kind, preview);
            // Antivenom/bandage are cleanses first, but they still heal. On Death's Door
            // that heal is the point - do not treat them as a wasted cleanse.
            if (!enemyTarget && preview.Heal > 0f
                && (target.DeathsDoor || target.HpPct <= 0.30f)
                && role != ItemRole.Heal)
                role = ItemRole.Heal;

            switch (role)
            {
                case ItemRole.Heal:
                    ScoreHeal(eval, target);
                    break;
                case ItemRole.Cleanse:
                    ScoreCleanse(eval, skillId, def, target);
                    break;
                case ItemRole.Stress:
                    ScoreStress(eval, target);
                    break;
                case ItemRole.Strip:
                    ScoreStrip(eval, target, tokens);
                    break;
                case ItemRole.Attack:
                    ScoreAttack(eval, preview, target, lastEnemy, livingEnemies);
                    break;
                case ItemRole.CorpseClear:
                    eval.Score = -40f;
                    eval.Reason = "item_clear_corpse_waste";
                    break;
                default:
                    ScoreBuff(eval, target, tokens, livingEnemies, lastEnemy || fightClosing);
                    break;
            }

            if (qty <= 1)
                eval.Score -= eval.Crisis ? 2f : 6f;
            else if (qty == 2)
                eval.Score -= eval.Crisis ? 0f : 2f;

            if (!eval.Crisis && livingEnemies <= 1 && target.Hp > 0f && target.Hp <= 6f && role == ItemRole.Attack)
                eval.Score -= 12f;

            eval.UseNow = eval.Score >= UseThreshold;
            if (!enemyTarget && preview.Heal > 0f && (target.DeathsDoor || target.HpPct <= 0.25f))
            {
                eval.UseNow = true;
                eval.Crisis = true;
            }
            if (string.IsNullOrEmpty(eval.Reason))
                eval.Reason = "item";
            return eval;
        }

        private enum ItemRole
        {
            Heal,
            Cleanse,
            Stress,
            Strip,
            Attack,
            Buff,
            CorpseClear
        }

        private static ItemRole ClassifyItem(string skillId, ActorDataSkill def, SkillKind kind, PreviewScore preview)
        {
            if (IsCorpseClear(skillId, def))
                return ItemRole.CorpseClear;
            if (LooksLike(skillId, def, "laudanum", "stress_heal", "horror"))
                return ItemRole.Stress;
            if (LooksLike(skillId, def, "antivenom", "bandage", "burn_salve", "medicinal", "blight", "bleed", "burn"))
                return ItemRole.Cleanse;
            if (LooksLike(skillId, def, "holy_water", "smelling_salt", "invigorating"))
                return ItemRole.Strip;
            if (kind == SkillKind.Heal || (preview != null && preview.Heal > 0f && kind != SkillKind.Attack))
                return ItemRole.Heal;
            if (kind == SkillKind.Attack)
                return ItemRole.Attack;
            return ItemRole.Buff;
        }

        private static void ScoreHeal(ItemEval eval, TargetInfo target)
        {
            if (target.DeathsDoor)
            {
                eval.Score = 90f;
                eval.Reason = "item_heal_dd";
                eval.Crisis = true;
                return;
            }

            var lethalSoon = target.Hp > 0f && (target.DiesToDot || target.Hp <= target.NextDot + 4f);
            if (target.HpPct <= 0.30f || lethalSoon)
            {
                eval.Score = 55f + (1f - target.HpPct) * 20f;
                eval.Reason = "item_heal_prevent";
                eval.Crisis = target.HpPct <= 0.25f || lethalSoon;
                return;
            }

            if (target.HpPct <= 0.45f)
            {
                eval.Score = 24f + (1f - target.HpPct) * 20f;
                eval.Reason = "item_heal";
                return;
            }

            eval.Score = -30f;
            eval.Reason = "item_heal_waste";
        }

        private static void ScoreCleanse(ItemEval eval, string skillId, ActorDataSkill def, TargetInfo target)
        {
            var amount = MatchingDot(skillId, def, target);
            if (amount <= 0f && target.NextDot <= 0f)
            {
                eval.Score = -40f;
                eval.Reason = "item_cleanse_none";
                return;
            }

            if (target.DiesToDot || (amount + 0.05f >= target.Hp && target.Hp > 0f && !target.DeathsDoor))
            {
                eval.Score = 80f;
                eval.Reason = "item_cleanse_lethal";
                eval.Crisis = true;
                return;
            }

            var tick = amount > 0f ? amount : target.NextDot;
            if (tick >= 4f)
            {
                eval.Score = 36f;
                eval.Reason = "item_cleanse";
                return;
            }

            if (tick > 0f && target.HpPct <= 0.55f)
            {
                eval.Score = 20f;
                eval.Reason = "item_cleanse";
                return;
            }

            eval.Score = -8f;
            eval.Reason = "item_cleanse_small";
        }

        private static void ScoreStress(ItemEval eval, TargetInfo target)
        {
            if (target.Stress >= 9f)
            {
                eval.Score = 55f;
                eval.Reason = "item_stress";
                eval.Crisis = true;
                return;
            }

            if (target.Stress >= 7f)
            {
                eval.Score = 24f;
                eval.Reason = "item_stress";
                return;
            }

            eval.Score = -25f;
            eval.Reason = "item_stress_waste";
        }

        private static void ScoreStrip(ItemEval eval, TargetInfo target, TokenEval tokens)
        {
            var bad = target.Stun || target.Blind || target.Weak || target.Vulnerable;
            if (target.Stun)
            {
                eval.Score = 32f;
                eval.Reason = "item_strip";
                eval.Crisis = true;
                return;
            }

            if (bad)
            {
                eval.Score = 22f;
                eval.Reason = "item_strip";
                return;
            }

            if (tokens != null && tokens.Score >= 8f)
            {
                eval.Score = tokens.Score;
                eval.Reason = "item_strip";
                return;
            }

            if (target.Combo)
            {
                eval.Score = -15f;
                eval.Reason = "item_strip_combo";
                return;
            }

            eval.Score = -30f;
            eval.Reason = "item_strip_waste";
        }

        // CSV: pouch_of_lye target_effects clear_corpse; performer stress_heal_1.
        // Use it when corpses are clogging ranks, especially the last living enemy.
        private static void ScoreCorpseClear(ItemEval eval, int livingEnemies)
        {
            if (livingEnemies <= 1)
            {
                eval.Score = 42f;
                eval.Reason = "item_clear_corpse";
                eval.Crisis = true;
                return;
            }

            if (livingEnemies <= 2)
            {
                eval.Score = 28f;
                eval.Reason = "item_clear_corpse";
                return;
            }

            eval.Score = 20f;
            eval.Reason = "item_clear_corpse";
        }

        private static bool IsCorpseClear(string skillId, ActorDataSkill def)
        {
            // Do not match target_is_not_corpse_hidden (substring trap).
            return LooksLike(skillId, def, "pouch_of_lye", "lye")
                   || HasCondition(def, "target_is_corpse_hidden");
        }

        private static void ScoreAttack(ItemEval eval, PreviewScore preview, TargetInfo target, bool lastEnemy, int livingEnemies)
        {
            if (target.Corpse)
            {
                eval.Score = -250f;
                eval.Reason = "item_skip_corpse";
                return;
            }

            eval.Score = 8f + preview.Damage;
            if (preview.Kills)
            {
                eval.Score += 28f;
                eval.Reason = "item_kill";
                return;
            }

            if (lastEnemy)
                eval.Score += 8f;
            else if (livingEnemies >= 3)
                eval.Score += 4f;

            eval.Reason = preview.Damage > 0f ? "item_damage" : "item_attack";
        }

        private static void ScoreBuff(ItemEval eval, TargetInfo target, TokenEval tokens, int livingEnemies, bool fightClosing)
        {
            var token = tokens != null ? tokens.Score : 0f;
            if (fightClosing)
            {
                eval.Score = token - 18f;
                eval.Reason = "item_buff_late";
                return;
            }

            eval.Score = token + (livingEnemies >= 2 ? 10f : 2f);
            eval.Reason = token >= 6f ? "item_buff" : "item_buff_small";
        }

        private static float MatchingDot(string skillId, ActorDataSkill def, TargetInfo target)
        {
            var blight = LooksLike(skillId, def, "antivenom", "blight");
            var bleed = LooksLike(skillId, def, "bandage", "bleed");
            var burn = LooksLike(skillId, def, "burn_salve", "burn");
            var any = LooksLike(skillId, def, "medicinal") || (!blight && !bleed && !burn);

            var amount = 0f;
            if (any || blight) amount += target.BlightDot;
            if (any || bleed) amount += target.BleedDot;
            if (any || burn) amount += target.BurnDot;
            return amount;
        }

        private static bool LooksLike(string skillId, ActorDataSkill def, params string[] keys)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                if (!string.IsNullOrEmpty(skillId) && skillId.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (HasTag(def, key))
                    return true;
                if (HasCondition(def, key))
                    return true;
            }
            return false;
        }

        private static bool HasTag(ActorDataSkill def, string key)
        {
            if (def == null || string.IsNullOrEmpty(key))
                return false;
            try
            {
                var tags = def.m_Tags;
                if (tags == null)
                    return false;
                for (var i = 0; i < tags.Count; i++)
                {
                    var tag = tags[i];
                    if (!string.IsNullOrEmpty(tag) && tag.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }

        private static bool HasCondition(ActorDataSkill def, string key)
        {
            if (def == null || string.IsNullOrEmpty(key))
                return false;
            return ListContains(GetMember(def, "m_AllConditionIds"), key)
                   || ListContains(GetMember(def, "m_AnyConditionIds"), key);
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null)
                return null;
            var type = obj.GetType();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var prop = type.GetProperty(name, flags);
            if (prop != null)
                return prop.GetValue(obj, null);
            var field = type.GetField(name, flags);
            return field != null ? field.GetValue(obj) : null;
        }

        private static bool ListContains(object listObj, string key)
        {
            var list = listObj as IEnumerable;
            if (list == null)
                return false;
            try
            {
                foreach (var item in list)
                {
                    var s = item as string;
                    if (!string.IsNullOrEmpty(s) && s.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
            catch { }
            return false;
        }
    }
}
