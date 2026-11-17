using System;
using System.Collections.Generic;
using Dd2Autobattler.Combat;

namespace Dd2Autobattler.Search
{
    /// <summary>
    /// Pure forward model over SimBoard: apply an action, advance the turn
    /// order of the current round, and estimate what non-performer actors do.
    ///
    /// Accuracy tiers (see notes/search.md for the measured numbers):
    /// - Current performer: the game's own preview (damage, kill, heal,
    ///   tokens, DoT), applied as an expectation over hit chance.
    /// - Later heroes / enemies: CSV skill damage ranges, launch / target
    ///   ranks and effects, token multipliers (Block, Block+, Vulnerable,
    ///   Dodge, Blind, Strength, Weak). Trinkets, quirks, path stats,
    ///   ordainment and buffs are not modelled.
    /// - Enemy skill / target choice: uniform over usable skills and legal
    ///   targets (Taunt respected). The real AI is not in the data.
    /// - Turn order: the game's remaining order for this round; kills and
    ///   stuns remove turns. No lookahead past the end of the round.
    /// Expected values throughout: Hp is expected HP, kills / Death's Door /
    /// death / stun are probabilities.
    /// </summary>
    internal static class ForwardModel
    {
        public const int UnmodeledSummon = 1;
        public const int UnmodeledClassChange = 2;
        public const int UnmodeledExtraTurn = 4;
        public const int UnmodeledUnknownSkill = 8;
        public const int UnmodeledRiposte = 16;

        internal const float DefaultDotResist = 0.2f;

        // -------- building --------

        public static SimBoard Build(TurnSnapshot snap, GameData data, EnemyFocus focus, SimBoard into = null)
        {
            var b = into ?? new SimBoard();
            b.Count = 0;
            b.QueueLen = 0;
            b.QueuePos = 0;
            b.Unmodeled = 0;
            b.Round = snap != null ? snap.Round : 0;
            b.Data = data;
            if (snap == null)
                return b;
            if (snap.Heroes != null)
            {
                foreach (var h in snap.Heroes)
                    AddActor(b, h, true, data, focus);
            }
            if (snap.Enemies != null)
            {
                foreach (var e in snap.Enemies)
                    AddActor(b, e, false, data, focus);
            }
            if (snap.RemainingTurns != null)
            {
                for (var i = 0; i < snap.RemainingTurns.Count && b.QueueLen < SimBoard.QueueCapacity; i++)
                {
                    var g = snap.RemainingTurns[i];
                    // The performer's current turn is not in the future.
                    if (i == 0 && g == snap.PerformerGuid)
                        continue;
                    var idx = b.IndexOf(g);
                    if (idx >= 0)
                        b.Queue[b.QueueLen++] = idx;
                }
            }
            return b;
        }

        private static void AddActor(SimBoard b, TargetInfo t, bool hero, GameData data, EnemyFocus focus)
        {
            if (t == null || t.Guid == 0 || b.Count >= SimBoard.Capacity)
                return;
            var a = b.Actors[b.Count++];
            var cls = data != null ? data.Class(t.ClassId) : null;
            a.Guid = t.Guid;
            a.Hero = hero;
            a.ClassId = t.ClassId;
            a.Skills = UsableSkillIds(t, hero, data);
            a.Rank = t.Rank;
            a.Size = Math.Max(Math.Max(1, t.Size), cls != null ? cls.Size : 1);
            a.Hp = Math.Max(0f, t.Hp);
            a.HpVar = 0f;
            a.HpMax = t.HpMax > 0f ? t.HpMax : (cls != null ? cls.HpMax : Math.Max(1f, t.Hp));
            a.AliveP = t.Living || t.Corpse ? 1f : 0f;
            a.Corpse = t.Corpse;
            a.Healthless = t.Healthless || (cls != null && cls.Healthless);
            a.DeathArmor = t.DeathArmor;
            a.Armor = CountExact(t, "deaths_door_armor");
            a.DoorP = t.DeathsDoor ? 1f : 0f;
            a.DeathP = hero && !t.Living && !t.Corpse ? 1f : 0f;
            a.DeathResist = cls != null ? cls.DeathResist : (hero ? 0.6f : 0f);
            a.Dot = Math.Max(0f, t.NextDot);
            a.Hot = Math.Max(0f, t.NextHot);
            a.StunP = t.Stun ? 1f : 0f;
            a.ResistStun = cls != null ? cls.ResistStun : 0.2f;
            a.ResistMove = cls != null ? cls.ResistMove : 0.2f;
            a.Block = CountExact(t, "block");
            a.BlockPlus = CountExact(t, "block_plus");
            a.Dodge = CountExact(t, "dodge");
            a.DodgePlus = CountExact(t, "dodge_plus");
            a.Vulnerable = t.Vulnerable;
            a.Weak = t.Weak;
            a.Strength = t.StrengthCount > 0;
            a.Taunt = t.Taunt;
            a.Combo = t.Combo;
            a.Blind = t.Blind;
            a.Stealth = t.Stealth;
            a.Riposte = t.Riposte;
            a.Focus = focus != null && !hero ? focus.ScoreOf(t.Guid) : 0f;
            a.Deferred = focus != null && !hero && focus.IsDeferred(t.Guid);
            a.MustKill = focus != null && !hero && focus.IsMustKillFirst(t.Guid);
            a.Acted = 0;
            a.DamageTaken = 0f;
            a.DamageDealt = 0f;
        }

        private static int CountExact(TargetInfo t, string id)
        {
            if (t.Tokens == null)
                return 0;
            var n = 0;
            for (var i = 0; i < t.Tokens.Count; i++)
            {
                if (string.Equals(t.Tokens[i], id, StringComparison.OrdinalIgnoreCase))
                    n++;
            }
            return n;
        }

        // Equipped skills the model can use: logged ids with CSV data, else
        // (enemies) the class file kit. Legacy "~legacy_kit/" ids are dropped.
        internal static List<string> UsableSkillIds(TargetInfo t, bool hero, GameData data)
        {
            var list = new List<string>();
            if (t.SkillIds != null)
            {
                foreach (var id in t.SkillIds)
                {
                    if (string.IsNullOrEmpty(id) || id.StartsWith("~", StringComparison.Ordinal))
                        continue;
                    list.Add(id);
                }
            }
            if (list.Count == 0 && !hero && data != null)
            {
                var kit = data.FileKit(t.ClassId);
                if (kit != null)
                    list.AddRange(kit);
            }
            return list;
        }

        // -------- token multipliers --------

        // Damage multiplier for a hit landing on t (Block / Block+ / Vulnerable).
        public static float DefenseMult(SimActor t)
        {
            var m = 1f;
            if (t.BlockPlus > 0)
                m *= 0.25f;
            else if (t.Block > 0)
                m *= 0.5f;
            if (t.Vulnerable)
                m *= 1.5f;
            return m;
        }

        public static float OffenseMult(SimActor p)
        {
            var m = 1f;
            if (p.Strength)
                m *= 1.5f;
            if (p.Weak)
                m *= 0.5f;
            return m;
        }

        // Chance an attack from p connects with t (Dodge / Dodge+ / Blind).
        public static float HitChance(SimActor p, SimActor t)
        {
            var hit = 1f;
            if (t.DodgePlus > 0)
                hit *= 0.25f;
            else if (t.Dodge > 0)
                hit *= 0.5f;
            if (p.Blind)
                hit *= 0.5f;
            return hit;
        }

        // -------- damage / death --------

        // Probability an integer damage roll in [lo, hi] reaches hp.
        public static float LethalP(float hp, float lo, float hi)
        {
            if (hi < lo)
                hi = lo;
            var need = (float)Math.Ceiling(hp - 0.05f);
            if (need <= 0f)
                return 1f;
            var l = (float)Math.Floor(lo + 0.001f);
            var h = (float)Math.Floor(hi + 0.001f);
            if (h - l < 0.5f)
                return lo + 0.05f >= hp ? 1f : 0f;
            return Clamp01((h - need + 1f) / (h - l + 1f));
        }

        // Same, when the target's HP is itself uncertain (variance hpVar from
        // earlier expected hits): normal approximation of roll - hp >= 0.
        public static float LethalP(float hp, float hpVar, float lo, float hi)
        {
            if (hpVar < 0.25f)
                return LethalP(hp, lo, hi);
            if (hi < lo)
                hi = lo;
            var mean = (lo + hi) * 0.5f - hp + 0.5f;
            var n = (float)Math.Floor(hi + 0.001f) - (float)Math.Floor(lo + 0.001f) + 1f;
            var rollVar = (n * n - 1f) / 12f;
            return Phi(mean / (float)Math.Sqrt(rollVar + hpVar));
        }

        // Standard normal CDF (Abramowitz-Stegun 7.1.26), deterministic.
        internal static float Phi(float z)
        {
            var x = Math.Abs(z) / 1.41421356f;
            var t = 1f / (1f + 0.3275911f * x);
            var y = 1f - (((((1.061405429f * t - 1.453152027f) * t) + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * (float)Math.Exp(-x * x);
            return z >= 0f ? 0.5f * (1f + y) : 0.5f * (1f - y);
        }

        private static float HitVariance(float q, float lo, float hi)
        {
            var m = (lo + hi) * 0.5f;
            var n = (float)Math.Floor(hi + 0.001f) - (float)Math.Floor(lo + 0.001f) + 1f;
            var v = n > 1f ? (n * n - 1f) / 12f : 0f;
            return q * (v + m * m) - q * q * m * m;
        }

        // A single hit, mean dmg (all multipliers in) rolled in [lo, hi],
        // landing with probability q. lo / hi <= 0: a fixed roll of dmg.
        public static void Damage(SimBoard b, SimActor t, float dmg, float q, float lo = -1f, float hi = -1f)
        {
            if (q <= 0f || t.Dead || t.Corpse || t.Healthless)
                return;
            q = Math.Min(1f, q);
            if (lo <= 0f || hi <= 0f)
            {
                lo = dmg;
                hi = dmg;
            }
            if (t.Hero)
            {
                DamageHero(t, dmg, q, lo, hi);
                return;
            }
            // Enemy on Death's Door: Death's Door armor absorbs one hit per
            // token; without armor the hit is a deathblow (death resist).
            if (OnDoor(t))
            {
                if (dmg <= 0f)
                    return;
                if (t.Armor > 0)
                {
                    if (q >= 0.5f)
                        t.Armor--;
                    return;
                }
                t.AliveP *= 1f - q * (1f - Clamp01(t.DeathResist));
                if (t.Dead)
                    OnDeath(b, t);
                return;
            }
            if (dmg <= 0f)
                return;
            var lethal = LethalP(t.Hp, t.HpVar, lo, hi);
            var pLethal = q * lethal;
            t.DamageTaken += q * Math.Min(dmg, t.Hp);
            // Expected damage of a non-lethal landed roll.
            var nonLethalMean = lethal >= 0.999f ? 0f : Math.Min(dmg, (lo + Math.Min(hi, t.Hp - 0.5f)) * 0.5f);
            if (nonLethalMean < 0f)
                nonLethalMean = 0f;
            if (t.Armor > 0)
            {
                // Armored enemies go to Death's Door at 0 HP instead of dying.
                t.DoorP += (1f - t.DoorP) * pLethal;
                t.Hp -= q * (1f - lethal) * nonLethalMean + pLethal * t.Hp;
                if (t.DoorP >= 0.5f || t.Hp <= 0.05f)
                {
                    t.Hp = 0f;
                    t.DoorP = 1f;
                }
                return;
            }
            var survive = 1f - pLethal;
            if (survive <= 0.0001f)
            {
                t.AliveP = 0f;
                OnDeath(b, t);
                return;
            }
            // Hp conditional on surviving: missed, or a non-lethal roll.
            var hpIfSurvive = ((1f - q) * t.Hp + q * (1f - lethal) * (t.Hp - nonLethalMean)) / survive;
            t.HpVar += HitVariance(q * (1f - lethal) / survive, lo, Math.Min(hi, Math.Max(lo, t.Hp - 0.5f)));
            t.AliveP *= survive;
            t.Hp = Math.Max(0.5f, hpIfSurvive);
            if (t.Dead)
                OnDeath(b, t);
        }

        // Heroes: the part already on Death's Door takes a deathblow check
        // (death resist); the rest may be knocked onto Death's Door. Hp is
        // the expected HP, HpVar its spread (so a 7 HP hero facing an
        // expected 7 is about a coin flip, not a certainty).
        private static void DamageHero(SimActor h, float dmg, float q, float lo, float hi)
        {
            if (dmg <= 0f)
                return;
            var door = h.Hp <= 0.05f ? 1f : h.DoorP;
            if (door > 0f)
            {
                var pk = q * door * (1f - Clamp01(h.DeathResist));
                h.DeathP += (1f - h.DeathP) * pk;
                h.AliveP = 1f - h.DeathP;
            }
            if (door >= 0.999f)
            {
                h.DoorP = 1f;
                h.Hp = 0f;
                return;
            }
            var lethal = LethalP(h.Hp, h.HpVar, lo, hi);
            h.DamageTaken += q * Math.Min(dmg, h.Hp);
            // Hp stays the expected HP of the branch NOT on Death's Door:
            // a miss, or a landed roll that left HP above 0.
            float hitLeft;
            if (h.HpVar < 0.25f)
            {
                var need = (float)Math.Ceiling(h.Hp - 0.05f);
                var maxSafe = Math.Min(hi, need - 1f);
                hitLeft = h.Hp - (lo + Math.Max(lo, maxSafe)) * 0.5f;
            }
            else
            {
                var n = (float)Math.Floor(hi + 0.001f) - (float)Math.Floor(lo + 0.001f) + 1f;
                var sd = (float)Math.Sqrt((n * n - 1f) / 12f + h.HpVar);
                hitLeft = TruncatedMeanAbove0(h.Hp - (lo + hi) * 0.5f, sd);
            }
            hitLeft = Math.Max(0.5f, hitLeft);
            var stay = 1f - q * lethal;
            h.DoorP = door + (1f - door) * q * lethal;
            h.HpVar += HitVariance(q * (1f - lethal), lo, hi);
            if (stay <= 0.001f || h.DoorP >= 0.999f)
            {
                h.Hp = 0f;
                h.DoorP = 1f;
                h.HpVar = 0f;
                return;
            }
            h.Hp = ((1f - q) * h.Hp + q * (1f - lethal) * hitLeft) / stay;
        }

        // E[X | X > 0] for X ~ N(mu, sd^2).
        internal static float TruncatedMeanAbove0(float mu, float sd)
        {
            if (sd <= 0.0001f)
                return mu;
            var a = mu / sd;
            var cdf = Phi(a);
            if (cdf < 1e-4f)
                return 0.5f;
            var pdf = 0.39894228f * (float)Math.Exp(-0.5f * a * a);
            return mu + sd * pdf / cdf;
        }

        // Enemies: on Death's Door (0 HP, armor or not). Heroes: DoorP >= 0.5.
        public static bool OnDoor(SimActor t)
        {
            return t.DoorP >= 0.5f || (t.Hp <= 0.05f && (t.Hero || t.Armor > 0 || t.DeathArmor));
        }
        internal static void OnDeath(SimBoard b, SimActor t)
        {
            if (t.Hero)
                return;
            // Corpse: same rank, corpse HP. No corpse: ranks behind shift up.
            var data = b.Data;
            var cls = data != null ? data.Class(t.ClassId) : null;
            if (cls != null && cls.HasCorpse)
            {
                // HpMax / Hp stay as they were (the eval reads them); the corpse
                // body only matters for rank occupancy here.
                t.Corpse = true;
                t.Dot = 0f;
                t.Hot = 0f;
                return;
            }
            var rank = t.Rank;
            t.Rank = -1;
            if (rank < 0)
                return;
            for (var i = 0; i < b.Count; i++)
            {
                var o = b.Actors[i];
                if (o == t || o.Hero || o.Rank < 0)
                    continue;
                if (o.Rank > rank)
                    o.Rank--;
            }
        }

        public static void Heal(SimActor t, float amount, float q)
        {
            if (amount <= 0f || q <= 0f || t.Dead || t.Corpse)
                return;
            t.Hp = Math.Min(t.HpMax, t.Hp + amount * q);
            if (t.Hero && t.DoorP > 0f)
                t.DoorP *= 1f - Math.Min(1f, q);
            if (q >= 0.999f)
                t.HpVar = 0f;
        }

        // Turn start: DoT ticks (after regen), Death's Door / death checks.
        public static void TickDot(SimBoard b, SimActor a)
        {
            var net = a.Dot - a.Hot;
            if (net < 0f)
            {
                Heal(a, -net, 1f);
                return;
            }
            if (net <= 0f || a.Dead || a.Corpse)
                return;
            Damage(b, a, net, 1f);
        }

        // Team-position move inside one team: positive = backward. Others shuffle.
        public static void Move(SimBoard b, SimActor a, int delta)
        {
            if (delta == 0 || a.Rank < 0)
                return;
            var maxRank = 0;
            for (var i = 0; i < b.Count; i++)
            {
                var o = b.Actors[i];
                if (o.Hero == a.Hero && o.Rank >= 0)
                    maxRank = Math.Max(maxRank, o.Rank);
            }
            var to = Math.Max(0, Math.Min(maxRank, a.Rank + delta));
            if (to == a.Rank)
                return;
            var from = a.Rank;
            for (var i = 0; i < b.Count; i++)
            {
                var o = b.Actors[i];
                if (o == a || o.Hero != a.Hero || o.Rank < 0)
                    continue;
                if (to > from && o.Rank > from && o.Rank <= to)
                    o.Rank--;
                else if (to < from && o.Rank >= to && o.Rank < from)
                    o.Rank++;
            }
            a.Rank = to;
        }

        // Swap two actors' ranks (hero *_move skills).
        public static void Swap(SimActor a, SimActor c)
        {
            var r = a.Rank;
            a.Rank = c.Rank;
            c.Rank = r;
        }

        // -------- root action: the game's preview --------

        // Apply the performer's legal action using the decider's processed
        // preview (living-hit clamp, guard bar, kill-from-HP already done).
        public static void ApplyPreview(SimBoard b, GameData data, int performer, string skillId, uint targetGuid,
                                        PreviewScore p, SkillSnap def, Recorder rec = null)
        {
            var actor = b.Actors[performer];
            actor.Acted++;
            var ti = b.IndexOf(targetGuid);
            var target = ti >= 0 ? b.Actors[ti] : null;
            var skill = data != null ? data.Skill(skillId) : null;
            if (p == null || !p.Ok)
            {
                if (rec != null)
                    rec.Step(actor, skillId, target, "no preview");
                return;
            }

            var isMove = (def != null && def.IsMove) || TurnDecider.Classify(skillId, def, p, target != null && !target.Hero) == SkillKind.Move;
            if (isMove && target != null && target.Hero == actor.Hero && target != actor)
            {
                Swap(actor, target);
                if (rec != null)
                    rec.Step(actor, skillId, target, "swap");
                return;
            }

            var hitP = p.HitChance > 0f ? Math.Min(1f, p.HitChance) : 1f;
            var dealt = 0f;
            var hitAny = false;
            // Per-guid hits (schema 2) else the click target / HitGuids with Damage.
            if (p.Hits != null && p.Hits.Count > 0)
            {
                for (var i = 0; i < p.Hits.Count; i++)
                {
                    var h = p.Hits[i];
                    if (h == null || h.Guid == 0)
                        continue;
                    var t = b.Find(h.Guid);
                    if (t == null || t.Hero == actor.Hero)
                        continue;
                    var before = t.Hp * t.AliveP;
                    // Per-hit roll range: the click range scaled to this hit's mean.
                    var ratio = p.Damage > 0.05f ? h.Damage / p.Damage : 1f;
                    Damage(b, t, h.Damage, hitP, p.DamageLow * ratio, p.DamageHigh * ratio);
                    dealt += before - t.Hp * t.AliveP;
                    hitAny = true;
                }
            }
            else if (target != null && target.Hero != actor.Hero && (p.Damage > 0f || OnDoor(target)))
            {
                if (p.HitGuids != null)
                {
                    for (var i = 0; i < p.HitGuids.Count; i++)
                    {
                        var t = b.Find(p.HitGuids[i]);
                        if (t == null || t.Hero == actor.Hero || t == target)
                            continue;
                        // AoE without per-hit numbers: same damage on each living body.
                        if (t.Up)
                            Damage(b, t, p.Damage, hitP, p.DamageLow, p.DamageHigh);
                    }
                }
                var before = target.Hp * target.AliveP;
                Damage(b, target, Math.Max(p.Damage, 0.01f), hitP, p.DamageLow, p.DamageHigh);
                dealt += before - target.Hp * target.AliveP;
                hitAny = true;
            }
            actor.DamageDealt += dealt;

            // Heals: the preview heal on the click target (friendly).
            if (target != null && target.Hero == actor.Hero && (p.Heal > 0f || p.HealsDeathsDoor))
                Heal(target, Math.Max(p.Heal, p.HealsDeathsDoor ? 1f : 0f), 1f);

            // Tokens / DoT on the target(s): every body a MultiHit skill hits
            // (the preview lists are per target), else the click target.
            // Non-damaging attacks still land their tokens.
            if (target != null)
            {
                var multi = skill != null && skill.MultiHit && target.Hero != actor.Hero;
                for (var i = 0; i < b.Count; i++)
                {
                    var t = b.Actors[i];
                    if (t != target && !(multi && t.Hero == target.Hero && t.Up && PreviewHits(p, t.Guid)))
                        continue;
                    ApplyTokenList(t, p.ApplyTarget, p, hitP, true);
                    RemoveTokenList(t, p.RemoveTarget);
                    if (t == target)
                        ConsumeOnHit(t, p.ConsumeTarget);
                    if (t.Hero != actor.Hero)
                    {
                        // Live DoT magnitude per tick (bleed / blight / burn), times land:
                        // this body's own DoT and resist when the preview has them per hit.
                        t.Dot += p.DotLandedOn(t.Guid) * hitP;
                    }
                }
            }
            ApplyTokenList(actor, p.ApplyPerformer, null, 1f, false);
            RemoveTokenList(actor, p.RemovePerformer);
            RemoveTokenList(actor, p.ConsumePerformer);
            if (hitAny)
            {
                actor.Strength = false;
                actor.Weak = false;
                actor.Blind = false;
            }

            // Moves the preview does not show: CSV performer / target m_Move.
            if (skill != null)
            {
                ApplyMoves(b, data, skill, actor, target, p.ResistOk ? 1f - Clamp01(p.ResistMove) : -1f, hitP);
                MarkUnmodeled(b, data, skill);
            }
            if (rec != null)
                rec.Step(actor, skillId, target, Describe(dealt, target));
        }

        private static bool PreviewHits(PreviewScore p, uint guid)
        {
            if (p.HitGuids != null && p.HitGuids.Contains(guid))
                return true;
            if (p.Hits != null)
            {
                for (var i = 0; i < p.Hits.Count; i++)
                {
                    if (p.Hits[i] != null && p.Hits[i].Guid == guid)
                        return true;
                }
            }
            return false;
        }

        private static void ApplyTokenList(SimActor t, List<string> ids, PreviewScore p, float q, bool onTarget)
        {
            if (ids == null || t == null || q <= 0f)
                return;
            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                if (string.IsNullOrEmpty(id))
                    continue;
                var land = p != null ? p.Land(id) : 1f;
                AddToken(t, id, q * land);
            }
        }

        internal static void AddToken(SimActor t, string id, float q)
        {
            if (q <= 0f)
                return;
            var key = id.ToLowerInvariant();
            if (key.StartsWith("stun") || key == "daze")
            {
                t.StunP += (1f - t.StunP) * Math.Min(1f, q);
                return;
            }
            if (q < 0.5f)
                return;
            switch (key)
            {
                case "block": t.Block++; break;
                case "block_plus": t.BlockPlus++; break;
                case "dodge": t.Dodge++; break;
                case "dodge_plus": t.DodgePlus++; break;
                case "vulnerable": t.Vulnerable = true; break;
                case "weak": t.Weak = true; break;
                case "strength": t.Strength = true; break;
                case "taunt": t.Taunt = true; break;
                case "combo": t.Combo = true; break;
                case "blind": t.Blind = true; break;
                case "stealth": t.Stealth = true; break;
                case "riposte": t.Riposte = true; break;
            }
        }

        private static void RemoveTokenList(SimActor t, List<string> ids)
        {
            if (ids == null || t == null)
                return;
            for (var i = 0; i < ids.Count; i++)
                RemoveToken(t, ids[i]);
        }

        internal static void RemoveToken(SimActor t, string id)
        {
            if (string.IsNullOrEmpty(id))
                return;
            switch (id.ToLowerInvariant())
            {
                case "block": if (t.Block > 0) t.Block--; break;
                case "block_plus": if (t.BlockPlus > 0) t.BlockPlus--; break;
                case "dodge": if (t.Dodge > 0) t.Dodge--; break;
                case "dodge_plus": if (t.DodgePlus > 0) t.DodgePlus--; break;
                case "vulnerable": t.Vulnerable = false; break;
                case "weak": t.Weak = false; break;
                case "strength": t.Strength = false; break;
                case "taunt": t.Taunt = false; break;
                case "combo": t.Combo = false; break;
                case "blind": t.Blind = false; break;
                case "stealth": t.Stealth = false; break;
                case "riposte": t.Riposte = false; break;
                case "stun": t.StunP = 0f; break;
            }
        }

        private static void ConsumeOnHit(SimActor t, List<string> ids)
        {
            RemoveTokenList(t, ids);
        }

        private static void ApplyMoves(SimBoard b, GameData data, GameData.SkillData skill, SimActor actor, SimActor target, float moveLand, float q)
        {
            for (var i = 0; i < skill.PerformerEffects.Count; i++)
            {
                var e = data.Effect(skill.PerformerEffects[i]);
                if (e != null && e.Move != 0 && e.Chance >= 0.5f)
                    Move(b, actor, e.Move);
            }
            if (target == null || target == actor)
                return;
            for (var i = 0; i < skill.TargetEffects.Count; i++)
            {
                var e = data.Effect(skill.TargetEffects[i]);
                if (e == null || e.Move == 0)
                    continue;
                var land = e.IgnoreResist || target.Hero == actor.Hero ? 1f
                    : (moveLand >= 0f ? moveLand : 1f - Clamp01(target.ResistMove));
                if (e.Chance * land * q >= 0.5f)
                    Move(b, target, e.Move);
            }
        }

        private static void MarkUnmodeled(SimBoard b, GameData data, GameData.SkillData skill)
        {
            MarkUnmodeled(b, data, skill.TargetEffects);
            MarkUnmodeled(b, data, skill.PerformerEffects);
        }

        private static void MarkUnmodeled(SimBoard b, GameData data, List<string> effects)
        {
            for (var i = 0; i < effects.Count; i++)
            {
                var e = data.Effect(effects[i]);
                if (e == null)
                    continue;
                if (!string.IsNullOrEmpty(e.Summon))
                    b.Unmodeled |= UnmodeledSummon;
                if (!string.IsNullOrEmpty(e.ChangeClass))
                    b.Unmodeled |= UnmodeledClassChange;
                if (e.AddTurn)
                    b.Unmodeled |= UnmodeledExtraTurn;
            }
        }

        // -------- model actions: CSV data --------

        // Can actor launch skill from its current rank?
        public static bool Launches(SimBoard b, SimActor actor, GameData.SkillData skill, SkillSnap def)
        {
            if (actor.Rank < 0)
                return false;
            // Game masks: (team position, size), same as the decider reads them.
            if (def != null && !def.MasksUnknown && def.LaunchMask != 0)
                return def.Launches(Math.Min(3, actor.Rank));
            if (skill == null || !skill.HasRanks)
                return false;
            return (skill.LaunchMask & SlotMask(b, actor)) != 0;
        }

        public static bool Reaches(SimBoard b, SimActor actor, GameData.SkillData skill, SkillSnap def, SimActor t)
        {
            if (t.Rank < 0)
                return false;
            if (def != null && !def.MasksUnknown && def.TargetMasks != null)
            {
                var launch = Math.Min(3, Math.Max(0, actor.Rank));
                return def.Targets(launch, Math.Min(3, t.Rank), Math.Max(1, t.Size));
            }
            if (skill == null)
                return false;
            return (skill.TargetMask & SlotMask(b, t)) != 0;
        }

        // Team position is ordinal (a size-2 body is one position); CSV
        // launch / target ranks are slots. Slots this actor covers, as bits.
        public static int SlotMask(SimBoard b, SimActor a)
        {
            if (a.Rank < 0)
                return 0;
            var start = 0;
            for (var i = 0; i < b.Count; i++)
            {
                var o = b.Actors[i];
                if (o != a && o.Hero == a.Hero && o.Rank >= 0 && o.Rank < a.Rank)
                    start += Math.Max(1, o.Size);
            }
            var mask = 0;
            for (var s = start; s < start + Math.Max(1, a.Size) && s < 4; s++)
                mask |= 1 << s;
            return mask;
        }

        // Expected damage one model hit of skill from p deals to t (before hit chance).
        public static float ModelDamage(SimActor p, GameData.SkillData skill, SimActor t)
        {
            if (skill == null || !skill.Damages)
                return 0f;
            var dmg = skill.DmgMean * (1f + 0.5f * Clamp01(skill.Crit));
            return dmg * OffenseMult(p) * DefenseMult(t);
        }

        // Apply skill from performer onto target with weight w (probability
        // this exact action happens). MultiHit skills hit every reachable body.
        public static void ApplyModelSkill(SimBoard b, GameData data, SimActor p, GameData.SkillData skill, SkillSnap def,
                                           SimActor target, float w, Recorder rec = null)
        {
            if (skill == null || target == null || w <= 0f)
                return;
            var friendly = skill.Friendly;
            var dealt = 0f;
            var hitAny = false;
            for (var i = 0; i < b.Count; i++)
            {
                var t = b.Actors[i];
                if (t != target && !(skill.MultiHit && t.Hero == target.Hero && t.Up && Reaches(b, p, skill, def, t)))
                    continue;
                if (!friendly)
                {
                    if (t.Hero == p.Hero || !t.Up)
                        continue;
                    var q = w * HitChance(p, t);
                    var dmg = ModelDamage(p, skill, t);
                    var mult = OffenseMult(p) * DefenseMult(t);
                    var before = t.Hero ? t.Hp : t.Hp * t.AliveP;
                    Damage(b, t, dmg, q, skill.DmgMin * mult, skill.DmgMax * mult);
                    dealt += Math.Max(0f, before - (t.Hero ? t.Hp : t.Hp * t.AliveP));
                    if (skill.Damages)
                    {
                        hitAny = true;
                        // Block / Vulnerable spend on a damaging hit; Dodge is
                        // removed whenever attacked.
                        if (w >= 0.5f)
                        {
                            if (t.BlockPlus > 0) t.BlockPlus--;
                            else if (t.Block > 0) t.Block--;
                            t.Vulnerable = false;
                            if (t.DodgePlus > 0) t.DodgePlus--;
                            else if (t.Dodge > 0) t.Dodge--;
                        }
                    }
                    if (t.Riposte && !t.Hero && p.Hero)
                        b.Unmodeled |= UnmodeledRiposte;
                    ApplyEffects(b, data, skill.TargetEffects, p, t, q);
                }
                else
                {
                    if (t.Hero != p.Hero || t.Dead || t.Corpse)
                        continue;
                    ApplyEffects(b, data, skill.TargetEffects, p, t, w);
                }
            }
            p.DamageDealt += dealt;
            if (w >= 0.5f)
            {
                ApplyEffects(b, data, skill.PerformerEffects, p, p, w);
                if (hitAny)
                {
                    p.Strength = false;
                    p.Weak = false;
                    p.Blind = false;
                }
            }
            MarkUnmodeled(b, data, skill);
            if (rec != null)
                rec.Step(p, skill.Id, target, (w < 0.999f ? "w" + w.ToString("0.00") + " " : "") + Describe(dealt, target));
        }

        private static void ApplyEffects(SimBoard b, GameData data, List<string> effects, SimActor p, SimActor t, float q)
        {
            for (var i = 0; i < effects.Count; i++)
            {
                var e = data.Effect(effects[i]);
                if (e == null)
                    continue;
                var chance = q * e.Chance;
                if (chance <= 0f)
                    continue;
                if (e.HealPct > 0f)
                    Heal(t, e.HealPct * t.HpMax, chance);
                if (e.HealAmount > 0f)
                    Heal(t, e.HealAmount, chance);
                if (e.DamageAmount > 0f)
                    Damage(b, t, e.DamageAmount, chance);
                if (!string.IsNullOrEmpty(e.DotAdd))
                {
                    var land = e.IgnoreResist || t.Hero == p.Hero ? 1f : 1f - DefaultDotResist;
                    t.Dot += data.DotPerTick(e) * chance * land;
                }
                if (!string.IsNullOrEmpty(e.TokenAdd) && e.TokenAmount >= 0)
                {
                    var id = e.TokenAdd;
                    var land = 1f;
                    if ((id.StartsWith("stun", StringComparison.OrdinalIgnoreCase) || id == "daze") && !e.IgnoreResist && t.Hero != p.Hero)
                        land = 1f - Clamp01(t.ResistStun);
                    AddToken(t, id, chance * land);
                }
                if (e.Move != 0 && t == p && chance >= 0.5f)
                    Move(b, t, e.Move);
                else if (e.Move != 0 && t != p && chance * (e.IgnoreResist ? 1f : 1f - Clamp01(t.ResistMove)) >= 0.5f)
                    Move(b, t, e.Move);
            }
        }

        // -------- turn advance --------

        // Next actor index in the queue, or -1 at the end of the round.
        public static int NextActor(SimBoard b)
        {
            while (b.QueuePos < b.QueueLen)
            {
                var idx = b.Queue[b.QueuePos++];
                var a = b.Actors[idx];
                if (a.Up)
                    return idx;
            }
            return -1;
        }

        // Turn start for actor idx: DoT tick, then stun. Returns the
        // probability the actor acts this turn (0 = skipped / died).
        public static float BeginTurn(SimBoard b, int idx)
        {
            var a = b.Actors[idx];
            a.Acted++;
            TickDot(b, a);
            if (!a.Up)
                return 0f;
            var act = 1f - a.StunP;
            a.StunP = 0f;
            if (a.Hero)
                act *= 1f - a.DeathP;
            else
                act *= a.AliveP;
            return act;
        }

        // Enemy turn: uniform over usable skills, uniform over legal targets
        // (Taunt narrows single-target attacks). act = probability it acts.
        public static void EnemyTurn(SimBoard b, GameData data, int idx, float act, Recorder rec = null)
        {
            var e = b.Actors[idx];
            if (act <= 0f || e.Skills == null)
                return;
            var usable = 0;
            for (var pass = 0; pass < 2; pass++)
            {
                for (var s = 0; s < e.Skills.Count; s++)
                {
                    var skill = data.Skill(e.Skills[s]);
                    if (!EnemyCanUse(b, e, skill))
                        continue;
                    if (pass == 0)
                    {
                        usable++;
                        continue;
                    }
                    var w = act / usable;
                    var nTargets = CountTargets(b, e, skill, out var tauntOnly);
                    if (nTargets == 0)
                        continue;
                    if (skill.MultiHit)
                    {
                        var first = FirstTarget(b, e, skill, tauntOnly);
                        ApplyModelSkill(b, data, e, skill, null, first, w, rec);
                        continue;
                    }
                    for (var i = 0; i < b.Count; i++)
                    {
                        var t = b.Actors[i];
                        if (!IsEnemyTarget(e, skill, t, tauntOnly, b))
                            continue;
                        ApplyModelSkill(b, data, e, skill, null, t, w / nTargets, rec);
                    }
                }
                if (usable == 0)
                    break;
            }
            if (usable == 0 && e.Skills.Count > 0)
                b.Unmodeled |= UnmodeledUnknownSkill;
        }

        // Model probability of (skill, target) for enemy idx, for validation.
        // pSkill: chance the model picks that skill id; pTarget: chance that
        // guid is hit given the skill (1 for a MultiHit that reaches it).
        public static void EnemyChoiceProb(SimBoard b, GameData data, int idx, string skillId, uint targetGuid,
                                           out float pSkill, out float pTarget, out bool modelled)
        {
            pSkill = 0f;
            pTarget = 0f;
            modelled = false;
            var e = b.Actors[idx];
            if (e.Skills == null)
                return;
            var usable = 0;
            GameData.SkillData chosen = null;
            for (var s = 0; s < e.Skills.Count; s++)
            {
                var skill = data.Skill(e.Skills[s]);
                if (!EnemyCanUse(b, e, skill))
                    continue;
                usable++;
                if (string.Equals(skill.Id, skillId, StringComparison.OrdinalIgnoreCase))
                    chosen = skill;
            }
            modelled = usable > 0;
            if (chosen == null)
                return;
            pSkill = 1f / usable;
            var n = CountTargets(b, e, chosen, out var tauntOnly);
            var t = b.Find(targetGuid);
            if (t == null || n == 0)
                return;
            if (chosen.Friendly)
            {
                pTarget = t.Hero == e.Hero ? 1f : 0f;
                return;
            }
            if (!IsEnemyTarget(e, chosen, t, tauntOnly, b))
                return;
            pTarget = chosen.MultiHit ? 1f : 1f / n;
        }

        internal static bool EnemyCanUse(SimBoard b, SimActor e, GameData.SkillData skill)
        {
            if (skill == null || !skill.HasRanks)
                return false;
            // Conditional specials (needs N tokens, phase gates) are not evaluated.
            for (var c = 0; c < skill.Conditions.Count; c++)
            {
                var id = skill.Conditions[c];
                if (id.StartsWith("performer_has", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("performer_is", StringComparison.OrdinalIgnoreCase)
                    || id.StartsWith("round_", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            if (!Launches(b, e, skill, null))
                return false;
            if (skill.Friendly)
                return true;
            return CountTargets(b, e, skill, out _) > 0;
        }

        private static int CountTargets(SimBoard b, SimActor e, GameData.SkillData skill, out bool tauntOnly)
        {
            tauntOnly = false;
            if (skill.Friendly)
                return 1;
            var n = 0;
            var taunts = 0;
            for (var i = 0; i < b.Count; i++)
            {
                var t = b.Actors[i];
                if (!IsEnemyTarget(e, skill, t, false, b))
                    continue;
                n++;
                if (t.Taunt)
                    taunts++;
            }
            if (taunts > 0 && !skill.MultiHit)
            {
                tauntOnly = true;
                return taunts;
            }
            return n;
        }

        private static bool IsEnemyTarget(SimActor e, GameData.SkillData skill, SimActor t, bool tauntOnly, SimBoard b)
        {
            if (skill.Friendly)
                return false;
            if (t.Hero == e.Hero || !t.Up)
                return false;
            if (t.Stealth && !skill.MultiHit)
                return false;
            if (tauntOnly && !t.Taunt)
                return false;
            return Reaches(b, e, skill, null, t);
        }

        private static SimActor FirstTarget(SimBoard b, SimActor e, GameData.SkillData skill, bool tauntOnly)
        {
            for (var i = 0; i < b.Count; i++)
            {
                var t = b.Actors[i];
                if (IsEnemyTarget(e, skill, t, tauntOnly, b))
                    return t;
            }
            return null;
        }

        // -------- helpers --------

        internal static float Clamp01(float v)
        {
            return v < 0f ? 0f : v > 1f ? 1f : v;
        }

        private static string Describe(float dealt, SimActor target)
        {
            if (target == null)
                return "";
            var s = dealt > 0.05f ? "-" + dealt.ToString("0.0") + " " : "";
            return s + "-> " + target;
        }
    }

    /// <summary>Optional trace of a model line (principal variation).</summary>
    internal sealed class Recorder
    {
        public readonly List<string> Lines = new List<string>();

        public void Step(SimActor actor, string skill, SimActor target, string what)
        {
            Lines.Add((actor != null ? (actor.Hero ? "H" : "E") + actor.Guid : "?") + " " + skill + " " + what);
        }

        public void Note(string what)
        {
            Lines.Add(what);
        }
    }
}
