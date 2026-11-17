using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Replay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public class SnapshotReplayTests
    {
        private static string GoldenDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Golden"); }
        }

        public static IEnumerable<object[]> GoldenFiles()
        {
            var dir = GoldenDir;
            if (!Directory.Exists(dir))
                yield break;
            foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
                yield return new object[] { Path.GetFileName(f) };
        }

        [Fact]
        public void GoldenCasesAreCheckedIn()
        {
            Assert.True(GoldenFiles().Count() >= 5, "expected golden fixtures in " + GoldenDir);
        }

        // Pinned real turns: expect / forbid picks (see Replay/GoldenCase.cs).
        [Theory]
        [MemberData(nameof(GoldenFiles))]
        public void GoldenCase(string file)
        {
            var g = Replay.GoldenCase.Load(Path.Combine(GoldenDir, file));
            ChosenAction chosen;
            var fail = g.Check(out chosen);
            Assert.True(fail == null, g.Name + " (" + g.Source + "): " + fail);
        }

        [Theory]
        [MemberData(nameof(GoldenFiles))]
        public void SnapshotJsonRoundTripsAndDecidesTheSame(string file)
        {
            var g = Replay.GoldenCase.Load(Path.Combine(GoldenDir, file));
            var first = SnapshotJson.Write(g.Snap);
            var again = SnapshotJson.Read(first);
            Assert.True(JToken.DeepEquals(first, SnapshotJson.Write(again)), "snapshot JSON is not stable: " + file);
            var a = TurnDecider.DecideSnapshot(g.Snap);
            var b = TurnDecider.DecideSnapshot(again);
            Assert.True(JToken.DeepEquals(new JArray(a.Rows.ToArray()), new JArray(b.Rows.ToArray())), "rows differ after round trip: " + file);
            Assert.Equal(a.Reason, b.Reason);
        }

        // Lookahead re-decides edited copies; the decider must not write to its input.
        [Theory]
        [MemberData(nameof(GoldenFiles))]
        public void DecideSnapshotDoesNotMutateInput(string file)
        {
            var g = Replay.GoldenCase.Load(Path.Combine(GoldenDir, file));
            var before = SnapshotJson.Write(g.Snap);
            TurnDecider.DecideSnapshot(g.Snap);
            TurnDecider.DecideSnapshot(g.Snap);
            Assert.True(JToken.DeepEquals(before, SnapshotJson.Write(g.Snap)), "DecideSnapshot mutated " + file);
        }

        [Fact]
        public void PreviewCloneCopiesEveryField()
        {
            var p = new PreviewScore();
            var n = 1;
            foreach (var f in typeof(PreviewScore).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (f.IsInitOnly)
                    continue;
                if (f.FieldType == typeof(float))
                    f.SetValue(p, n++ + 0.5f);
                else if (f.FieldType == typeof(bool))
                    f.SetValue(p, true);
                else if (f.FieldType == typeof(uint))
                    f.SetValue(p, (uint)n++);
                else if (f.FieldType == typeof(string))
                    f.SetValue(p, "s" + n++);
                else if (f.FieldType == typeof(JObject))
                    f.SetValue(p, new JObject());
                else
                    throw new InvalidOperationException("PreviewCloneCopiesEveryField: handle " + f.Name);
            }
            p.ApplyTarget.Add("combo");
            p.ApplyPerformer.Add("riposte");
            p.ConsumeTarget.Add("block");
            p.ConsumePerformer.Add("strength");
            p.RemoveTarget.Add("dodge");
            p.RemovePerformer.Add("blind");
            p.HitGuids.Add(7);
            p.Hits.Add(new PreviewHit { Guid = 7, Damage = 3f, Kills = true });

            var c = p.Clone();
            foreach (var f in typeof(PreviewScore).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var a = f.GetValue(p);
                var b = f.GetValue(c);
                if (a is System.Collections.IList la)
                {
                    Assert.NotSame(a, b);
                    var lb = (System.Collections.IList)b;
                    Assert.Equal(la.Count, lb.Count);
                    continue;
                }
                Assert.True(Equals(a, b), "Clone dropped " + f.Name);
            }
            Assert.NotSame(p.Hits[0], c.Hits[0]);
            Assert.Equal(3f, c.Hits[0].Damage);
        }

        [Fact]
        public void MemoryCloneCopiesEveryField()
        {
            var m = new MemoryState
            {
                LastEnemyGuid = 1,
                SetupSpent = true,
                ItemActorGuid = 2,
                ItemsUsedThisActorTurn = 3,
                Round = 4,
                TaprootHitsThisRound = 5
            };
            m.ComboSpendersActed.Add(6);
            m.CrisisHealsThisRound.Add(7);
            m.ReachWalkedThisRound.Add(8);
            var c = m.Clone();
            foreach (var f in typeof(MemoryState).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                var a = f.GetValue(m);
                var b = f.GetValue(c);
                if (a is HashSet<uint> sa)
                {
                    Assert.NotSame(a, b);
                    Assert.True(sa.SetEquals((HashSet<uint>)b), "Clone dropped " + f.Name);
                    continue;
                }
                Assert.True(Equals(a, b), "Clone dropped " + f.Name);
            }
        }

        private static JObject LegacyTurnRecord(int index, uint actor, string name)
        {
            var hero = new JObject { ["guid"] = actor, ["name"] = name, ["class"] = "highwayman", ["team"] = 0, ["hp"] = 30, ["hp_max"] = 40, ["hp_pct"] = 0.75, ["living"] = true };
            return new JObject
            {
                ["type"] = "turn",
                ["mode"] = "auto",
                ["turn_index"] = index,
                ["actor"] = hero,
                ["heroes"] = new JArray(hero),
                ["enemies"] = new JArray(new JObject { ["guid"] = 10, ["class"] = "bandit", ["team"] = 1, ["hp"] = 20, ["hp_max"] = 20, ["hp_pct"] = 1.0, ["living"] = true }),
                ["legal"] = new JArray(new JObject { ["skill"] = "slash", ["kind"] = "Attack", ["target"] = 10, ["enemy"] = true, ["preview_ok"] = true, ["dmg"] = 5, ["hit"] = 1, ["hit_n"] = 1, ["score"] = 0 }),
                ["chosen"] = new JObject { ["skill"] = "slash", ["target"] = 10 }
            };
        }

        [Fact]
        public void ReplayEngineInfersRoundsForLegacyTurns()
        {
            var path = Path.Combine(Path.GetTempPath(), "dd2replay-" + Guid.NewGuid().ToString("N"), "decisions.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                File.WriteAllLines(path, new[]
                {
                    new JObject { ["type"] = "fight_start", ["fight"] = "f" }.ToString(Formatting.None),
                    LegacyTurnRecord(1, 1, "A").ToString(Formatting.None),
                    LegacyTurnRecord(2, 2, "B").ToString(Formatting.None),
                    LegacyTurnRecord(3, 1, "A").ToString(Formatting.None),
                    LegacyTurnRecord(4, 1, "A").ToString(Formatting.None)
                });
                var engine = new ReplayEngine();
                engine.RunFile(path);
                Assert.Equal(new[] { 1, 1, 2, 3 }, engine.Turns.Select(t => t.Round).ToArray());
                Assert.All(engine.Turns, t => Assert.False(t.Full));
                Assert.All(engine.Turns, t => Assert.True(t.Agree));
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path), true);
            }
        }

        // A hand-built schema-2 snapshot: the kill on the 3 HP enemy beats a chip.
        private static TurnSnapshot TinySnapshot()
        {
            var hero = new TargetInfo { Guid = 1, Name = "Dismas", ClassId = "highwayman", Living = true, Size = 1, Hp = 30f, HpMax = 40f, HpPct = 0.75f, SkillIds = new List<string> { "slash" } };
            var weak = new TargetInfo { Guid = 10, ClassId = "bandit", Team = 1, Living = true, Size = 1, Hp = 3f, HpMax = 20f, HpPct = 0.15f, Rank = 0 };
            var full = new TargetInfo { Guid = 11, ClassId = "bandit", Team = 1, Living = true, Size = 1, Hp = 20f, HpMax = 20f, HpPct = 1f, Rank = 1 };
            var snap = new TurnSnapshot { Schema = TurnSnapshot.CurrentSchema, PerformerGuid = 1, Performer = hero };
            snap.Heroes.Add(hero);
            snap.Enemies.Add(weak);
            snap.Enemies.Add(full);
            snap.Memory.Round = 3;
            var slash = new SkillSnap { Id = "slash", OwnerSize = 1, LaunchMask = 0xF, TargetMasks = new int[16] };
            slash.Role.Attacks = true;
            snap.Skills["slash"] = slash;
            foreach (var t in new[] { weak, full })
            {
                var p = new PreviewScore { Ok = true, Damage = 6f, DamageLow = 5f, DamageHigh = 7f, DamageValid = true, HitChance = 0.95f };
                p.HitGuids.Add(t.Guid);
                p.Hits.Add(new PreviewHit { Guid = t.Guid, Damage = 6f });
                snap.Legal.Add(new LegalSnap { Skill = "slash", Target = t.Guid, Preview = p });
            }
            snap.Reindex();
            return snap;
        }

        [Fact]
        public void PureDecideOnHandBuiltSnapshot()
        {
            var snap = TinySnapshot();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.NotNull(r.Chosen);
            Assert.Equal(10u, r.Chosen.TargetGuid);
            Assert.True(r.Candidates[0].Preview.Kills, "6 dmg on a 3 HP bar is a kill (NoteKillFromHp)");
            Assert.Equal("slash", r.Ranked()[0].SkillId);
        }

        // Old ComboHitMask: per enemy rank try { size1 || size2 }; a throw
        // skips that rank; every rank thrown = unknown.
        [Fact]
        public void ComboHitMaskSkipsRanksWhoseQueryThrew()
        {
            var snap = TinySnapshot();
            var slash = snap.Skills["slash"];
            slash.TargetThrew = new int[16];
            slash.TargetMasks[0 * 4 + 0] = 0b0110; // launch 0, size 1: ranks 1, 2
            slash.TargetThrew[0 * 4 + 0] = 0b0001; // rank 0 size-1 query threw
            Assert.Equal(0b0110, PartyKit.Scan(snap).Heroes[0].AttackHitRanks);

            slash.TargetThrew[0 * 4 + 0] = 0b1111;
            Assert.Equal(PartySynergy.ComboRanksUnknown, PartyKit.Scan(snap).Heroes[0].AttackHitRanks);

            slash.TargetThrew = null;
            slash.TargetMasks = null;
            Assert.Equal(PartySynergy.ComboRanksUnknown, PartyKit.Scan(snap).Heroes[0].AttackHitRanks);
        }

        [Fact]
        public void SnapshotJsonKeepsNonDefaultHitChanceAndHits()
        {
            var snap = TinySnapshot();
            snap.Legal[1].Preview.HitChance = 0f;
            var back = SnapshotJson.Read(SnapshotJson.Write(snap));
            Assert.Equal(0.95f, back.Legal[0].Preview.HitChance);
            Assert.Equal(0f, back.Legal[1].Preview.HitChance);
            Assert.Single(back.Legal[0].Preview.Hits);
            Assert.Equal(6f, back.Legal[0].Preview.Hits[0].Damage);
            Assert.Same(back.Heroes[0], back.Performer);
            Assert.True(back.FullFidelity);
        }

        [Fact]
        public void LegacyTurnRebuildsAndFlagsMissingInputs()
        {
            var turn = JObject.Parse(@"{
              'type':'turn','mode':'auto','turn_index':1,
              'actor':{'guid':1,'name':'Dismas','class':'highwayman','team':0,'rank':0,'hp':30,'hp_max':40,'hp_pct':0.75,'living':true,'tokens':[{'id':'strength','dur':2}]},
              'heroes':[{'guid':1,'name':'Dismas','class':'highwayman','team':0,'rank':0,'hp':30,'hp_max':40,'hp_pct':0.75,'living':true,'tokens':[{'id':'strength','dur':2}]}],
              'enemies':[{'guid':10,'class':'bandit','team':1,'rank':0,'hp':8,'hp_max':20,'hp_pct':0.4,'living':true,'combo':true,'tokens':[{'id':'combo','dur':1}]},
                         {'guid':11,'class':'bandit_corpse','team':1,'rank':1,'hp':0,'hp_max':20,'hp_pct':0,'living':true,'corpse':true,'tokens':[]}],
              'legal':[
                {'skill':'hwm_open_vein','kind':'Attack','target':10,'enemy':true,'score':40,'preview_ok':true,'dmg':4,'hit':1,'kills':false,'hit_n':2,'apply_bleed':6,'apply':[],'consume':['combo']},
                {'skill':'flg_fester','kind':'Attack','target':11,'enemy':true,'corpse':true,'score':28,'preview_ok':true,'dmg':0,'hit':1,'hit_n':1},
                {'skill':'bandages','kind':'Heal','target':1,'enemy':false,'score':-30,'preview_ok':true,'heal':0,'item':true,'item_free':true,'item_qty':2}
              ],
              'chosen':{'skill':'hwm_open_vein','target':10,'score':40,'reason':'spend_combo'},
              'synergy':{'combo_spenders':['Dismas'],'combo_best':12,'attackers':['Dismas'],'healers':[],'stunners':[],'bleed':true,'blight':false,'burn':false},
              'focus':{'enemies':[{'name':'10','class':'bandit','boss':false,'add':false,'why':'trash'}]}
            }".Replace('\'', '"'));
            var snap = LegacyTurn.Build(turn, new MemoryState { Round = 2 });
            Assert.False(snap.FullFidelity);
            Assert.Contains("no_turn_order", snap.Degraded);
            Assert.Equal(2, snap.Round);
            // Combo bleed double undone, hit count kept.
            Assert.Equal(3f, snap.Legal[0].Preview.ApplyBleed);
            Assert.Equal(2, snap.Legal[0].Preview.HitGuids.Count);
            // Corpse click that did not take the -250 skip is a corpse clear.
            Assert.True(snap.Skill("flg_fester").ClearCorpseEffect);
            // Logged Heal with 0 restore keeps classifying as Heal.
            Assert.True(snap.Legal[2].Preview.HealValid);
            Assert.True(snap.Skill("bandages").IsItemSkill);
            Assert.Equal(2, snap.RemainingQty("bandages"));
            Assert.Equal(1, snap.Performer.StrengthCount);
            Assert.Single(snap.Threats);

            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal(SkillKind.Heal, r.Candidates[2].Kind);
            Assert.Equal(3, r.Candidates.Count);
        }

        [Fact]
        public void BaselineDiffReportsChangedPicks()
        {
            var a = new ReplayTurn { Key = "s#1/t1", Replayed = new ReplayPick { Skill = "slash", Target = 10 } };
            var b = new ReplayTurn { Key = "s#1/t2", Replayed = new ReplayPick { Skill = "slash", Target = 11 } };
            var baseline = Baseline.From(new[] { a, b }, "test");
            var path = Path.Combine(Path.GetTempPath(), "dd2replay-baseline-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                baseline.Save(path);
                var loaded = Baseline.Load(path);
                var now = new[]
                {
                    a,
                    new ReplayTurn { Key = "s#1/t2", Replayed = new ReplayPick { Skill = "stab", Target = 11 } },
                    new ReplayTurn { Key = "s#1/t3", Replayed = null }
                };
                var diff = loaded.Diff(now);
                Assert.Equal(2, diff.Compared);
                Assert.Equal(1, diff.Unchanged);
                Assert.Single(diff.Changed);
                Assert.Equal("s#1/t2", diff.Changed[0].Key);
                Assert.Equal("slash", diff.Changed[0].Before.Skill);
                Assert.Equal(new[] { "s#1/t3" }, diff.Added);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ReplayEngineReplaysSchema2Turn()
        {
            var snap = TinySnapshot();
            var schema2 = new JObject
            {
                ["type"] = "turn",
                ["mode"] = "auto",
                ["turn_index"] = 1,
                ["actor"] = new JObject { ["guid"] = 1, ["name"] = "Dismas", ["class"] = "highwayman" },
                ["chosen"] = new JObject { ["skill"] = "slash", ["target"] = 10, ["score"] = 0, ["reason"] = "x" },
                ["legal"] = new JArray(TurnDecider.DecideSnapshot(snap).Rows.ToArray()),
                ["snap"] = SnapshotJson.Write(snap)
            };
            var path = Path.Combine(Path.GetTempPath(), "dd2replay-" + Guid.NewGuid().ToString("N"), "decisions.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                File.WriteAllLines(path, new[]
                {
                    new JObject { ["type"] = "fight_start", ["fight"] = "test_fight" }.ToString(Formatting.None),
                    schema2.ToString(Formatting.None)
                });
                var engine = new ReplayEngine();
                engine.RunFile(path);
                Assert.Equal(1, engine.Summary.Turns);
                Assert.Equal(1, engine.Summary.Full);
                Assert.Equal(1, engine.Summary.AgreeFull);
                Assert.Equal(engine.Turns[0].Rows, engine.Turns[0].RowsScoreMatch);
                Assert.EndsWith("#1/t1", engine.Turns[0].Key);
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(path), true);
            }
        }
    }
}
