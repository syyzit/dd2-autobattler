using System;
using System.Collections.Generic;
using Dd2Autobattler.Combat;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Replay
{
    /// <summary>
    /// Best-effort TurnSnapshot from a schema-1 turn record (no "snap" field).
    /// The old log has post-scoring rows, not the decider's inputs, so some
    /// inputs are approximated and every gap is named in snap.Degraded.
    /// </summary>
    internal static class LegacyTurn
    {
        // Synthetic per-hero skill that carries the logged synergy roles
        // (attackers / healers / combo spenders). Inert for every other rule:
        // not friendly-heal, no tags, no rank masks.
        internal const string KitSkillPrefix = "~legacy_kit/";

        public static readonly string[] AlwaysDegraded =
        {
            "legacy_schema1",
            "no_preview_hits",      // per-guid damage/kill hits: corpse splash, guard bar, living clamp
            "no_remove_target",     // target token removes: strip / peel / Combo strip
            "no_equipped_skills",   // reach walks / heal walks / Fall walk cannot fire
            "kit_from_names",       // party kit from synergy name lists
            "no_turn_order",        // leave-chip, stun_next
            "no_dot_split",         // bleed/blight/burn per actor
            "no_debuff_move_resist",
            "memory_inferred"       // round, once-per-round gates, setup/item budgets
        };

        public static TurnSnapshot Build(JObject turn, MemoryState memory, CsvSkillData csv = null)
        {
            if (turn == null)
                return null;
            var legal = turn["legal"] as JArray;
            var actorJson = turn["actor"] as JObject;
            if (legal == null || actorJson == null)
                return null;

            var snap = new TurnSnapshot
            {
                Schema = 1,
                PerformerGuid = actorJson.Value<uint?>("guid") ?? 0u,
                Memory = memory != null ? memory.Clone() : new MemoryState()
            };
            snap.Degraded.AddRange(AlwaysDegraded);
            // Roles and launch/target ranks are never available; tags and
            // conditions come from the game CSVs when the replayer has them.
            snap.Degraded.Add(csv != null && csv.Count > 0 ? "skill_roles_ranks_missing" : "no_skill_defs");

            foreach (var a in Arr(turn["heroes"]))
                snap.Heroes.Add(Actor(a));
            foreach (var a in Arr(turn["enemies"]))
                snap.Enemies.Add(Actor(a));
            snap.Reindex();
            snap.Performer = snap.Find(snap.PerformerGuid) ?? Actor(actorJson);

            ApplyFocus(snap, turn["focus"] as JObject);
            ApplyLegal(snap, legal, csv);
            ApplyKit(snap, turn["synergy"] as JObject);
            snap.Reindex();
            return snap;
        }

        // Inverse of GameSnapshot.Actor plus Describe's token-derived fields.
        internal static TargetInfo Actor(JToken token)
        {
            var o = token as JObject;
            var info = new TargetInfo();
            if (o == null)
                return info;
            info.Guid = o.Value<uint?>("guid") ?? 0u;
            info.Name = o.Value<string>("name");
            info.ClassId = o.Value<string>("class");
            info.Team = o.Value<int?>("team") ?? 0;
            info.Rank = o.Value<int?>("rank") ?? 0;
            info.Hp = o.Value<float?>("hp") ?? 0f;
            info.HpMax = o.Value<float?>("hp_max") ?? 0f;
            info.HpPct = o.Value<float?>("hp_pct") ?? 0f;
            info.Stress = o.Value<float?>("stress") ?? 0f;
            info.Living = o.Value<bool?>("living") ?? false;
            info.DeathsDoor = B(o, "deaths_door");
            info.Corpse = B(o, "corpse");
            info.Healthless = B(o, "healthless");
            info.NextDot = o.Value<float?>("next_dot") ?? 0f;
            info.NextHot = o.Value<float?>("next_hot") ?? 0f;
            info.DiesToDot = B(o, "dies_to_dot");
            info.DeathArmor = B(o, "death_armor");
            info.Stealth = B(o, "stealth");
            info.Riposte = B(o, "riposte");
            info.Dodge = B(o, "dodge");
            info.Combo = B(o, "combo");
            info.Stun = B(o, "stun");
            info.Vulnerable = B(o, "vulnerable");
            info.Weak = B(o, "weak");
            info.Tangled = B(o, "tangled");
            info.TangledLock = B(o, "tangled_lock");
            info.EyesFocus = o.Value<int?>("eyes_focus") ?? 0;
            info.LungInflate = B(o, "lung_inflate");
            info.Taunt = B(o, "taunt");
            info.CallOfTheDeep = B(o, "call_of_the_deep");
            info.Size = 1;
            info.Tokens = new List<string>();
            foreach (var t in Arr(o["tokens"]))
            {
                var id = t is JObject tj ? tj.Value<string>("id") : t.Type == JTokenType.String ? (string)t : null;
                if (!string.IsNullOrEmpty(id) && id != "?")
                    info.Tokens.Add(id);
            }
            // Same CountToken keys as GameSnapshot.Describe.
            info.Blind = info.CountToken("blind") > 0;
            info.StrengthCount = info.CountToken("strength");
            info.BlockCount = info.CountToken("block");
            info.DodgeCount = info.CountToken("dodge");
            info.TorsoTarget = info.CountToken("torso_target") > 0;
            info.PositiveTokens = info.StrengthCount + info.BlockCount + info.DodgeCount
                                  + (info.Riposte ? 1 : 0) + info.CountToken("crit");
            info.Winded = info.CountToken("winded");
            info.RuinReady = info.CountToken("ruin_ready") > 0;
            info.Pain = info.CountToken("pain_heal") > 0;
            info.MoreMore = info.CountToken("more_more") > 0;
            info.Conviction = info.CountToken("conviction");
            info.AggressiveStance = info.CountToken("aggressive_stance") > 0;
            info.DefensiveStance = info.CountToken("defensive_stance") > 0;
            info.BeastMode = info.CountToken("beast_mode") > 0;
            return info;
        }

        // Logged focus is post-note. Feed its class flags back as raw threats
        // and let EnemyFocus re-run the cited notes (they key off class ids).
        private static void ApplyFocus(TurnSnapshot snap, JObject focus)
        {
            var living = new List<TargetInfo>();
            foreach (var e in snap.Enemies)
            {
                if (e != null && e.Living && !e.Corpse)
                    living.Add(e);
            }
            var logged = focus != null ? Arr(focus["enemies"]) : new List<JToken>();
            if (focus == null)
                snap.Degraded.Add("no_focus");
            for (var i = 0; i < living.Count; i++)
            {
                var a = living[i];
                var f = i < logged.Count ? logged[i] as JObject : null;
                if (f != null && !string.Equals(f.Value<string>("class"), a.ClassId, StringComparison.OrdinalIgnoreCase))
                {
                    f = null;
                    AddOnce(snap.Degraded, "focus_unmatched");
                }
                var why = f != null ? f.Value<string>("why") ?? "" : "";
                var t = new EnemyThreat
                {
                    Guid = a.Guid,
                    Name = string.IsNullOrEmpty(a.Name) ? a.Guid.ToString() : a.Name,
                    ClassId = a.ClassId,
                    Boss = f != null && B(f, "boss"),
                    Summons = f != null && B(f, "summons"),
                    Resurrects = f != null && B(f, "resurrects"),
                    Supports = f != null && B(f, "supports"),
                    Add = f != null && B(f, "add"),
                    Size = why.IndexOf("large", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 1,
                    DiesToDot = a.DiesToDot,
                    Dodge = a.Dodge,
                    Riposte = a.Riposte,
                    HpPct = a.HpPct,
                    Worship = a.CountToken("worship")
                };
                if (t.Size >= 2)
                    a.Size = 2;
                snap.Threats.Add(t);
            }
            // Dreaming General logs its exact round / Taproot count.
            if (focus != null && focus["dg_round"] != null)
            {
                snap.Memory.Round = focus.Value<int>("dg_round");
                snap.Memory.TaprootHitsThisRound = focus.Value<int?>("dg_taproot_hits") ?? 0;
            }
        }

        private sealed class SkillFacts
        {
            public bool Move, Support, Item, Free, Cursed, ClearsCorpse;
            public int Qty;
        }

        private static void ApplyLegal(TurnSnapshot snap, JArray legal, CsvSkillData csv)
        {
            var facts = new Dictionary<string, SkillFacts>();
            var order = new List<string>();
            foreach (var token in legal)
            {
                var row = token as JObject;
                if (row == null)
                    continue;
                var skill = row.Value<string>("skill");
                var target = row.Value<uint?>("target") ?? 0u;
                if (string.IsNullOrEmpty(skill) || target == 0)
                    continue;
                SkillFacts f;
                if (!facts.TryGetValue(skill, out f))
                {
                    f = new SkillFacts();
                    facts[skill] = f;
                    order.Add(skill);
                }
                var kind = row.Value<string>("kind") ?? "";
                f.Move |= kind == "Move";
                f.Support |= kind == "Support";
                f.Item |= B(row, "item");
                f.Free |= B(row, "item_free");
                f.Cursed |= B(row, "curse");
                f.Qty = Math.Max(f.Qty, row.Value<int?>("item_qty") ?? 0);

                var targetInfo = snap.Find(target);
                // CSV clear_corpse is not logged. A non-item attack into a
                // corpse that did not score the -250 skip (and is not a DoT
                // finish) was a corpse clear (Purge, Fester, ...).
                if (kind == "Attack" && B(row, "enemy") && B(row, "corpse") && !B(row, "item")
                    && F(row, "score") > -100f
                    && F(row, "apply_bleed") + F(row, "apply_blight") + F(row, "apply_burn") <= 0.05f)
                    f.ClearsCorpse = true;
                var p = new PreviewScore
                {
                    Ok = B(row, "preview_ok"),
                    Error = row.Value<string>("error"),
                    Damage = F(row, "dmg"),
                    DamageLow = F(row, "dmg_lo"),
                    DamageHigh = F(row, "dmg_hi"),
                    Heal = F(row, "heal"),
                    HitChance = row.Value<float?>("hit") ?? 1f,
                    Kills = B(row, "kills"),
                    Blocked = B(row, "blocked"),
                    GuardGuid = row.Value<uint?>("guard") ?? 0u,
                    ResistOk = true,
                    ResistBleed = F(row, "res_bleed"),
                    ResistBlight = F(row, "res_blight"),
                    ResistBurn = F(row, "res_burn"),
                    ResistStun = F(row, "res_stun"),
                    ApplyBleed = F(row, "apply_bleed"),
                    ApplyBlight = F(row, "apply_blight"),
                    ApplyBurn = F(row, "apply_burn")
                };
                p.DamageValid = p.Damage > 0f || p.DamageLow > 0f || p.DamageHigh > 0f;
                p.HealValid = p.Heal > 0f;
                // Classify said Heal with 0 restore: the preview had HealValid.
                if (kind == "Heal" && p.Heal <= 0f && !B(row, "enemy"))
                    p.HealValid = true;
                p.HealsDeathsDoor = p.Heal > 0f && targetInfo != null && targetInfo.DeathsDoor;
                // Logged rows are after the Combo bleed double; undo it.
                if (targetInfo != null && targetInfo.Combo && p.ApplyBleed >= 2f
                    && KitSafety.IdHas(skill, "open_vein"))
                    p.ApplyBleed *= 0.5f;
                AddAll(p.ApplyTarget, row["apply"]);
                AddAll(p.ConsumeTarget, row["consume"]);
                AddAll(p.ApplyPerformer, row["apply_self"]);
                AddAll(p.RemovePerformer, row["remove_self"]);
                AddAll(p.ConsumePerformer, row["consume_self"]);
                // Hit guids beyond the click target are unknown; keep the count.
                p.HitGuids.Add(target);
                var hitN = row.Value<int?>("hit_n") ?? 1;
                for (var i = 1; i < hitN; i++)
                    p.HitGuids.Add(0u);

                snap.Legal.Add(new LegalSnap
                {
                    Skill = skill,
                    Target = target,
                    // Logged "stealthed" already ORs target stealth; the
                    // decider ORs it again, so this is the same flag.
                    Stealthed = B(row, "stealthed"),
                    Preview = p
                });
            }

            var performerSkills = new List<string>();
            foreach (var id in order)
            {
                var f = facts[id];
                var def = new SkillSnap
                {
                    Id = id,
                    IsMove = f.Move,
                    IsFriendly = f.Support,
                    IsItemSkill = f.Item,
                    IsFree = f.Free,
                    ClearCorpseEffect = f.ClearsCorpse,
                    OwnerSize = 1,
                    MasksUnknown = true
                };
                def.Role.Attacks = !def.IsFriendly && !def.IsMove;
                var row = csv != null ? csv.Find(id) : null;
                if (row != null)
                {
                    def.Tags.AddRange(row.Tags);
                    def.Conditions.AddRange(row.Conditions);
                    foreach (var e in row.Effects)
                    {
                        if (e.IndexOf("clear_corpse", StringComparison.OrdinalIgnoreCase) >= 0)
                            def.ClearCorpseEffect = true;
                    }
                }
                snap.Skills[id] = def;
                performerSkills.Add(id);
                if (f.Cursed)
                    snap.CursedSkills.Add(id);
                if (f.Item)
                    snap.ItemQty[id] = f.Qty;
            }
            snap.Performer.SkillIds = performerSkills;
        }

        // One synthetic kit skill per living hero from the synergy name lists.
        private static void ApplyKit(TurnSnapshot snap, JObject synergy)
        {
            if (synergy == null)
            {
                snap.Degraded.Add("no_synergy");
                return;
            }
            var spenders = Names(synergy["combo_spenders"]);
            var attackers = Names(synergy["attackers"]);
            var healers = Names(synergy["healers"]);
            var stunners = Names(synergy["stunners"]);
            var best = synergy.Value<float?>("combo_best") ?? 0f;
            var bleed = B(synergy, "bleed");
            var blight = B(synergy, "blight");
            var burn = B(synergy, "burn");
            foreach (var hero in snap.Heroes)
            {
                if (hero == null || !hero.Living || hero.Corpse)
                    continue;
                var name = string.IsNullOrEmpty(hero.Name) ? hero.Guid.ToString() : hero.Name;
                var id = KitSkillPrefix + hero.Guid;
                var role = new SkillRole
                {
                    Attacks = attackers.Contains(name),
                    Heals = healers.Contains(name),
                    Stuns = stunners.Contains(name),
                    SpendsCombo = spenders.Contains(name),
                    ComboSpendValue = spenders.Contains(name) ? best : 0f,
                    // Party-level flags only; per hero is unknown.
                    Bleed = bleed,
                    Blight = blight,
                    Burn = burn
                };
                snap.Skills[id] = new SkillSnap { Id = id, Role = role, OwnerSize = 1, MasksUnknown = true };
                if (hero.SkillIds == null)
                    hero.SkillIds = new List<string>();
                hero.SkillIds.Insert(0, id);
            }
        }

        private static HashSet<string> Names(JToken token)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in Arr(token))
            {
                if (t.Type == JTokenType.String)
                    set.Add((string)t);
            }
            return set;
        }

        private static List<JToken> Arr(JToken token)
        {
            var list = new List<JToken>();
            if (token is JArray arr)
            {
                foreach (var t in arr)
                {
                    if (t != null)
                        list.Add(t);
                }
            }
            return list;
        }

        private static void AddAll(List<string> dest, JToken token)
        {
            foreach (var t in Arr(token))
            {
                if (t.Type == JTokenType.String && !string.IsNullOrEmpty((string)t))
                    dest.Add((string)t);
            }
        }

        private static void AddOnce(List<string> list, string value)
        {
            if (!list.Contains(value))
                list.Add(value);
        }

        private static bool B(JObject o, string key)
        {
            var t = o[key];
            return t != null && t.Type == JTokenType.Boolean && (bool)t;
        }

        private static float F(JObject o, string key)
        {
            var t = o[key];
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer))
                return 0f;
            return (float)t;
        }
    }
}
