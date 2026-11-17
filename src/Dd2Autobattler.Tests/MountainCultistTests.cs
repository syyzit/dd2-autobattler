using System;
using System.Collections.Generic;
using System.IO;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Replay;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using ScoredAction = Dd2Autobattler.Combat.TurnDecider.ScoredAction;

namespace Dd2Autobattler.Tests
{
    // cultist_mountain_* (the Deacon fight right before the mountain boss, no inn
    // between): TurnSnapshot.FightId plumbing and the fight-gated stress rules
    // (notes/cited-bosses.md, Cultists, "Mountain cultist fight").
    public class MountainCultistTests
    {
        private const uint Maa = 1, Hwm = 2, Gr = 3, Pd = 4, Deacon = 10, Cherub = 11, Herald = 12;

        private static string GoldenDir
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Golden"); }
        }

        private static TargetInfo Hero(uint guid, string cls, int rank, float stress, float hp = 30f, float max = 40f)
        {
            return new TargetInfo { Guid = guid, ClassId = cls, Team = 0, Rank = rank, Living = true, Hp = hp, HpMax = max, HpPct = hp / max, Stress = stress, Tokens = new List<string>() };
        }

        private static TargetInfo Enemy(uint guid, string cls, int rank, float hp = 30f)
        {
            return new TargetInfo { Guid = guid, ClassId = cls, Team = 1, Rank = rank, Living = true, Hp = hp, HpMax = 41f, HpPct = hp / 41f, Tokens = new List<string>() };
        }

        // MAA rank 0, Highwayman rank 1, Grave Robber 2, Plague Doctor 3.
        private static TurnSnapshot Snap(float maa, float hwm, float gr, float pd, bool herald = false, string fight = "cultist_mountain_1_7")
        {
            var snap = new TurnSnapshot { FightId = fight };
            snap.Heroes.Add(Hero(Maa, "man_at_arms", 0, maa, 50f, 55f));
            snap.Heroes.Add(Hero(Hwm, "highwayman", 1, hwm));
            snap.Heroes.Add(Hero(Gr, "grave_robber", 2, gr));
            snap.Heroes.Add(Hero(Pd, "plague_doctor", 3, pd));
            snap.Enemies.Add(Enemy(Deacon, "cultist_deacon", 0));
            snap.Enemies.Add(Enemy(Cherub, "cultist_cherub", 2, 14f));
            if (herald)
                snap.Enemies.Add(Enemy(Herald, "cultist_herald", 3, 18f));
            SyncThreats(snap);
            snap.PerformerGuid = Maa;
            snap.Performer = snap.Find(Maa);
            return snap;
        }

        // Class / boss facts EnemyFocus.Scan reads (LiveTurnReader fills these).
        private static void SyncThreats(TurnSnapshot snap)
        {
            snap.Threats.Clear();
            foreach (var e in snap.Enemies)
            {
                if (e == null || !e.Living || e.Corpse)
                    continue;
                snap.Threats.Add(new EnemyThreat { Guid = e.Guid, ClassId = e.ClassId, Boss = e.ClassId == "cultist_deacon", HpPct = e.HpPct, Size = 1 });
            }
            snap.Reindex();
        }

        private static ScoredAction Act(string skill, SkillKind kind, TargetInfo target, float score, float dmg = 0f, bool kills = false)
        {
            return new ScoredAction
            {
                SkillId = skill,
                TargetGuid = target.Guid,
                Kind = kind,
                EnemyTarget = target.Team == 1,
                Target = target,
                Preview = new PreviewScore { Ok = true, Damage = dmg, Kills = kills },
                Score = score
            };
        }

        private static EnemyFocus Focus(TurnSnapshot snap)
        {
            return EnemyFocus.Scan(snap);
        }

        // ---------- FightId plumbing ----------

        [Fact]
        public void FightIdPrefix()
        {
            Assert.True(EnemyFocus.IsMountainCultistFight("cultist_mountain_1_5"));
            Assert.True(EnemyFocus.IsMountainCultistFight("cultist_mountain_2_3"));
            Assert.False(EnemyFocus.IsMountainCultistFight(null));
            Assert.False(EnemyFocus.IsMountainCultistFight(""));
            Assert.False(EnemyFocus.IsMountainCultistFight("cultist_guardian_biome_1_altar_2"));
            Assert.False(EnemyFocus.IsMountainCultistFight("mountain_boss_brain_4"));
            Assert.Null(TurnSnapshot.NormalizeFightId("none"));
            Assert.Null(TurnSnapshot.NormalizeFightId("fight"));
            Assert.Equal("cultist_mountain_1_1", TurnSnapshot.NormalizeFightId("cultist_mountain_1_1"));
        }

        [Fact]
        public void FightIdRoundTripsAndOldSnapshotsStayTheSame()
        {
            var snap = Snap(0f, 0f, 0f, 0f);
            var json = SnapshotJson.Write(snap);
            Assert.Equal("cultist_mountain_1_7", json.Value<string>("FightId"));
            Assert.Equal("cultist_mountain_1_7", SnapshotJson.Read(json).FightId);

            // A snapshot logged before the field: no key, reads as null, and
            // writing it back adds nothing (old golden JSON is unchanged).
            snap.FightId = null;
            var old = SnapshotJson.Write(snap);
            Assert.Null(old["FightId"]);
            var back = SnapshotJson.Read(old);
            Assert.Null(back.FightId);
            Assert.True(JToken.DeepEquals(old, SnapshotJson.Write(back)));
        }

        [Fact]
        public void FillFightIdPrefersFightStartAndKeepsALoggedId()
        {
            var snap = new TurnSnapshot();
            ReplayEngine.FillFightId(snap, "cultist_mountain_1_5", "other");
            Assert.Equal("cultist_mountain_1_5", snap.FightId);
            ReplayEngine.FillFightId(snap, "mountain_boss_brain", null);
            Assert.Equal("cultist_mountain_1_5", snap.FightId);
            var none = new TurnSnapshot();
            ReplayEngine.FillFightId(none, "none", "cultist_mountain_1_2");
            Assert.Equal("cultist_mountain_1_2", none.FightId);
            var nothing = new TurnSnapshot();
            ReplayEngine.FillFightId(nothing, "fight", "none");
            Assert.Null(nothing.FightId);
        }

        // A schema-2 snapshot logged before FightId existed gets the id of the
        // fight_start around it, so fight-gated rules replay on old logs.
        [Fact]
        public void ReplayEngineFillsMissingFightIdFromFightStart()
        {
            var g = GoldenCase.Load(Path.Combine(GoldenDir, "mountain_bolster_nine_stress_pd.json"));
            var snap = g.Snap;
            snap.FightId = null;
            var withoutId = TurnDecider.DecideSnapshot(snap);
            Assert.NotEqual("maa_bolster", withoutId.Chosen.SkillId);

            var turn = new JObject
            {
                ["type"] = "turn",
                ["mode"] = "auto",
                ["turn_index"] = 1,
                ["actor"] = new JObject { ["guid"] = snap.PerformerGuid, ["name"] = "Barristan", ["class"] = "man_at_arms" },
                ["chosen"] = new JObject { ["skill"] = withoutId.Chosen.SkillId, ["target"] = withoutId.Chosen.TargetGuid, ["score"] = 0, ["reason"] = "x" },
                ["legal"] = new JArray(withoutId.Rows.ToArray()),
                ["snap"] = SnapshotJson.Write(snap)
            };
            Assert.Null(turn["snap"]["FightId"]);
            var dir = Path.Combine(Path.GetTempPath(), "dd2replay-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(dir, "decisions.jsonl");
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllLines(path, new[]
                {
                    new JObject { ["type"] = "fight_start", ["fight"] = "cultist_mountain_1_7" }.ToString(Formatting.None),
                    turn.ToString(Formatting.None)
                });
                var engine = new ReplayEngine { KeepSnapshots = true };
                engine.RunFile(path);
                Assert.Single(engine.Turns);
                Assert.Equal("cultist_mountain_1_7", engine.Turns[0].Snapshot.FightId);
                Assert.Equal("maa_bolster", engine.Turns[0].Replayed.Skill);
                Assert.Equal("mountain_bolster", engine.Turns[0].Replayed.Reason);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        // ---------- gating: the same board decides differently by fight id ----------

        [Theory]
        [InlineData("mountain_bolster_nine_stress_pd.json")]
        [InlineData("mountain_bolster_heals_two.json")]
        [InlineData("mountain_no_taunt_onto_tank.json")]
        [InlineData("mountain_recover_vs_last_cherub.json")]
        public void RulesOnlyFireInTheMountainCultistFight(string file)
        {
            var g = GoldenCase.Load(Path.Combine(GoldenDir, file));
            var gated = TurnDecider.DecideSnapshot(g.Snap).Chosen;
            foreach (var other in new[] { null, "cultist_guardian_biome_1_altar_2", "mountain_boss_brain_4" })
            {
                var snap = SnapshotJson.Clone(g.Snap);
                snap.FightId = other;
                var plain = TurnDecider.DecideSnapshot(snap).Chosen;
                Assert.False(plain.SkillId == gated.SkillId && plain.TargetGuid == gated.TargetGuid,
                    file + ": same pick without the fight id (" + (other ?? "null") + ")");
            }
        }

        // ---------- Taunt ----------

        [Fact]
        public void TauntShieldsOnlyAMuchMoreStressedReachableHero()
        {
            // Highwayman (rank 1) 6 vs MAA 1: shield.
            var snap = Snap(1f, 6f, 0f, 0f);
            Assert.True(TurnDecider.MountainTauntShields(snap, snap.Performer, Focus(snap)));
            // 2 points is not enough (two taunted hits are ~3 stress).
            snap = Snap(3f, 5f, 0f, 0f);
            Assert.False(TurnDecider.MountainTauntShields(snap, snap.Performer, Focus(snap)));
            // A 9-stress PD on rank 3 is out of Sundering Steel's reach (ranks 1-2)...
            snap = Snap(0f, 0f, 0f, 9f);
            Assert.False(TurnDecider.MountainTauntShields(snap, snap.Performer, Focus(snap)));
            // ... unless a Herald (Clarion Call, ranks 1-4) is up.
            snap = Snap(0f, 0f, 0f, 9f, herald: true);
            Assert.True(TurnDecider.MountainTauntShields(snap, snap.Performer, Focus(snap)));
            // A dead Herald does not count.
            snap.Find(Herald).Living = false;
            SyncThreats(snap);
            Assert.False(TurnDecider.MountainTauntShields(snap, snap.Performer, Focus(snap)));
        }

        [Fact]
        public void BlockedTauntIsDockedAndNeverTheRiposteOpen()
        {
            var snap = Snap(3f, 1f, 3f, 2f);
            var focus = Focus(snap);
            var retribution = Act("maa_retribution", SkillKind.Support, snap.Performer, 24f);
            var crush = Act("maa_crush_u", SkillKind.Attack, snap.Find(Deacon), -20f, dmg: 5f);
            var list = new List<ScoredAction> { retribution, crush };
            TurnDecider.ApplyMountainCultistTaunt(list, snap, snap.Performer, focus);
            Assert.True(retribution.TauntBlocked);
            Assert.Equal(24f - TurnDecider.MountainTauntPenalty, retribution.Score);
            Assert.Equal(-20f, crush.Score);
            Assert.False(crush.TauntBlocked);

            // Another fight: untouched.
            snap.FightId = "cultist_mash_3";
            var r2 = Act("maa_retribution", SkillKind.Support, snap.Performer, 24f);
            TurnDecider.ApplyMountainCultistTaunt(new List<ScoredAction> { r2 }, snap, snap.Performer, Focus(snap));
            Assert.False(r2.TauntBlocked);
            Assert.Equal(24f, r2.Score);
        }

        // ---------- Bolster ----------

        [Fact]
        public void BolsterHealsFollowTheCsvThresholds()
        {
            var maa = Hero(Maa, "man_at_arms", 0, 4f);
            var t = Hero(Pd, "plague_doctor", 3, 4f);
            Assert.Equal(0f, TurnDecider.BolsterStressHealed("maa_bolster", t, maa));
            t.Stress = 5f;
            Assert.Equal(1f, TurnDecider.BolsterStressHealed("maa_bolster", t, maa));
            maa.Stress = 5f;
            Assert.Equal(2f, TurnDecider.BolsterStressHealed("maa_bolster", t, maa));
            Assert.Equal(4f, TurnDecider.BolsterStressHealed("maa_bolster_u", t, maa));
        }

        [Fact]
        public void BolsterBeatsANonKillSwingOnASevenStressHeroOrForTwo()
        {
            var snap = Snap(3f, 1f, 1f, 9f);
            var focus = Focus(snap);
            var swing = Act("maa_rampart_u", SkillKind.Attack, snap.Find(Deacon), 159f, dmg: 4f);
            var bolsterPd = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), 57f);
            var bolsterGr = Act("maa_bolster", SkillKind.Support, snap.Find(Gr), -23f);
            var list = new List<ScoredAction> { swing, bolsterPd, bolsterGr };
            var pick = TurnDecider.MountainBolsterOverSwing(swing, list, snap, snap.Performer, focus, "attack", 2);
            Assert.Same(bolsterPd, pick);
            Assert.Equal("mountain_bolster", pick.NoteReason);

            // 1 point on a 5-6 stress hero is not worth the swing...
            snap = Snap(3f, 1f, 1f, 6f);
            list = new List<ScoredAction> { swing, Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -23f) };
            Assert.Same(swing, TurnDecider.MountainBolsterOverSwing(swing, list, snap, snap.Performer, Focus(snap), "attack", 2));
            // ... 2 points (target and MAA both at 5+) are.
            snap = Snap(6f, 1f, 1f, 5f);
            var two = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -145f);
            list = new List<ScoredAction> { swing, two };
            Assert.Same(two, TurnDecider.MountainBolsterOverSwing(swing, list, snap, snap.Performer, Focus(snap), "attack", 1));
        }

        [Fact]
        public void BolsterLeavesKillsCrisisAndOtherFightsAlone()
        {
            var snap = Snap(3f, 1f, 1f, 9f);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), 57f);
            var kill = Act("maa_crush_u", SkillKind.Attack, snap.Find(Cherub), 120f, dmg: 9f, kills: true);
            var list = new List<ScoredAction> { kill, bolster };
            Assert.Same(kill, TurnDecider.MountainBolsterOverSwing(kill, list, snap, snap.Performer, Focus(snap), "attack", 2));

            // A cited gate / reason is not a swing.
            var swing = Act("maa_rampart_u", SkillKind.Attack, snap.Find(Deacon), 159f, dmg: 4f);
            list = new List<ScoredAction> { swing, bolster };
            Assert.Same(swing, TurnDecider.MountainBolsterOverSwing(swing, list, snap, snap.Performer, Focus(snap), "crisis_heal", 2));
            swing.NoteReason = "cover_guard";
            Assert.Same(swing, TurnDecider.MountainBolsterOverSwing(swing, list, snap, snap.Performer, Focus(snap), "attack", 2));
            swing.NoteReason = null;

            // Death's Door / an ally at 25% or less: the crisis and cover gates own the turn.
            snap.Find(Hwm).DeathsDoor = true;
            Assert.Same(swing, TurnDecider.MountainBolsterOverSwing(swing, list, snap, snap.Performer, Focus(snap), "attack", 2));
            snap.Find(Hwm).DeathsDoor = false;
            snap.Find(Gr).Hp = 8f; snap.Find(Gr).HpPct = 0.2f;
            Assert.Same(swing, TurnDecider.MountainBolsterOverSwing(swing, list, snap, snap.Performer, Focus(snap), "attack", 2));
            snap.Find(Gr).Hp = 30f; snap.Find(Gr).HpPct = 0.75f;
            Assert.Same(bolster, TurnDecider.MountainBolsterOverSwing(swing, list, snap, snap.Performer, Focus(snap), "attack", 2));

            // Not this fight.
            snap.FightId = "cultist_guardian_biome_1_altar_2";
            Assert.Same(swing, TurnDecider.MountainBolsterOverSwing(swing, list, snap, snap.Performer, Focus(snap), "attack", 2));
        }

        // ---------- last-enemy recovery ----------

        [Fact]
        public void IdleTurnOnTheLastDeaconBecomesRecoveryOnlyHere()
        {
            var snap = Snap(3f, 1f, 1f, 6f);
            snap.Enemies.RemoveAt(1); // Deacon alone: boss + must-kill
            SyncThreats(snap);
            var focus = Focus(snap);
            var pass = Act("pass_stress", SkillKind.Pass, snap.Performer, -50f);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -65f);
            var list = new List<ScoredAction> { pass, bolster };
            Assert.False(TurnDecider.IsSafeLastEnemy(snap.Find(Deacon), focus));
            var pick = TurnDecider.RecoverOverIdle(pass, list, focus, snap, snap.Performer, 1, 1, true);
            Assert.Same(bolster, pick);
            Assert.Equal("recover", pick.NoteReason);

            // A 0-damage Blind on him is not idle here (50% evade on his next hit).
            bolster.NoteReason = null;
            var blind = Act("pd_blinding_gas", SkillKind.Attack, snap.Find(Deacon), 10f);
            list.Add(blind);
            Assert.Same(blind, TurnDecider.RecoverOverIdle(blind, list, focus, snap, snap.Performer, 1, 1, true));

            // Other fights keep the boss exclusion.
            snap.FightId = null;
            Assert.Same(pass, TurnDecider.RecoverOverIdle(pass, list, Focus(snap), snap, snap.Performer, 1, 1, true));
        }
    }
}
