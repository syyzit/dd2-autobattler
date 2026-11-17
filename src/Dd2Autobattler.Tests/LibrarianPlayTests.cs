using System.Collections.Generic;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Logging;
using Xunit;

namespace Dd2Autobattler.Tests
{
    // Librarian (city_dungeon_3_*): party reach walk, self-move reach, ignited race,
    // book stacks do not hold the battle open; notes/cited-bosses.md.
    public class LibrarianPlayTests
    {
        private const uint Gr = 1, Hwm = 2, Maa = 3, Pd = 4;
        private const uint StackA = 10, StackB = 11, StackC = 12, Lib = 20;

        // Rank masks are 0-based (bit r = rank r + 1). Target masks only depend on
        // the enemy rank here (one row reused for every launch rank / size).
        private static SkillSnap Attack(string id, int launch, int target, bool heals = false)
        {
            var masks = new int[16];
            for (var i = 0; i < masks.Length; i++)
                masks[i] = target;
            return new SkillSnap { Id = id, LaunchMask = launch, TargetMasks = masks, Role = new SkillRole { Attacks = true, Heals = heals } };
        }

        private static SkillSnap Move(string id)
        {
            return new SkillSnap { Id = id, IsMove = true, LaunchMask = 0xF, TargetMasks = new int[16], Role = new SkillRole() };
        }

        private static TargetInfo Hero(uint g, string cls, int rank, params string[] skills)
        {
            return new TargetInfo { Guid = g, ClassId = cls, Team = 0, Living = true, Hp = 30f, HpMax = 35f, HpPct = 30f / 35f, Rank = rank, SkillIds = new List<string>(skills) };
        }

        private static TargetInfo Enemy(uint g, string cls, int rank, float hp)
        {
            return new TargetInfo { Guid = g, ClassId = cls, Team = 1, Living = true, Hp = hp, HpMax = hp, HpPct = 1f, Rank = rank };
        }

        // Starter kit, CSV ranks: Pistol / Thrown Dagger launch 2-4 hit 2-4,
        // Crush launch 1-2 hit 1-3, Wicked Slice / Pick launch 1-3 hit 1-2,
        // Noxious Blast launch 2-4 hit 1-2.
        private static TurnSnapshot Board(uint performer, int gr, int hwm, int maa, int pd, int libRank, bool ignited = false)
        {
            var snap = new TurnSnapshot { PerformerGuid = performer, FightId = ignited ? "city_dungeon_3_a" : "city_dungeon_3_b" };
            snap.Heroes.Add(Hero(Gr, "grave_robber", gr, "gr_thrown_dagger_u", "gr_pick_to_the_face_u", "gr_move"));
            snap.Heroes.Add(Hero(Hwm, "highwayman", hwm, "hwm_pistol_shot_u", "hwm_wicked_slice_u", "hwm_duelists_advance", "hwm_move"));
            snap.Heroes.Add(Hero(Maa, "man_at_arms", maa, "maa_crush_u", "maa_move"));
            snap.Heroes.Add(Hero(Pd, "plague_doctor", pd, "pd_noxious_blast_u", "pd_move"));
            snap.Performer = snap.Heroes.Find(h => h.Guid == performer);
            var stacks = new[] { "fanatic_librarian_stack_m", "fanatic_librarian_stack_l", "fanatic_librarian_stack_m" };
            if (!ignited)
            {
                for (var r = 0; r < libRank; r++)
                    snap.Enemies.Add(Enemy(StackA + (uint)r, stacks[r], r, 15f));
            }
            snap.Enemies.Add(Enemy(Lib, ignited ? "fanatic_librarian_ignited" : "fanatic_librarian", libRank, 60f));
            foreach (var e in snap.Enemies)
                snap.Threats.Add(new EnemyThreat { Guid = e.Guid, ClassId = e.ClassId, Boss = e.Guid == Lib });
            snap.Skills["gr_thrown_dagger_u"] = Attack("gr_thrown_dagger_u", 0xE, 0xE);
            snap.Skills["gr_pick_to_the_face_u"] = Attack("gr_pick_to_the_face_u", 0x7, 0x3);
            snap.Skills["hwm_pistol_shot_u"] = Attack("hwm_pistol_shot_u", 0xE, 0xE);
            snap.Skills["hwm_wicked_slice_u"] = Attack("hwm_wicked_slice_u", 0x7, 0x3);
            snap.Skills["hwm_duelists_advance"] = Attack("hwm_duelists_advance", 0xE, 0x7);
            snap.Skills["maa_crush_u"] = Attack("maa_crush_u", 0x3, 0x7);
            snap.Skills["pd_noxious_blast_u"] = Attack("pd_noxious_blast_u", 0xE, 0x3);
            foreach (var m in new[] { "gr_move", "hwm_move", "maa_move", "pd_move" })
                snap.Skills[m] = Move(m);
            snap.Reindex();
            return snap;
        }

        private static void Legal(TurnSnapshot snap, string skill, uint target, float dmg = 0f, float heal = 0f)
        {
            var p = new PreviewScore { Ok = true, Damage = dmg, DamageValid = dmg > 0f, HitChance = 1f, Heal = heal, HealValid = heal > 0f };
            p.HitGuids.Add(target);
            snap.Legal.Add(new LegalSnap { Skill = skill, Target = target, Preview = p });
        }

        [Fact]
        public void MoveModelSlidesTheHeroesBetween()
        {
            // Categorize order GR, HWM, MAA, PD; MAA rank 3 -> 1.
            var snap = Board(Maa, 0, 1, 2, 3, 3);
            var ranks = LibrarianPlay.RanksAfterMove(snap, snap.Performer, 0);
            Assert.Equal(0, ranks[Maa]);
            Assert.Equal(1, ranks[Gr]);
            Assert.Equal(2, ranks[Hwm]);
            Assert.Equal(3, ranks[Pd]);
        }

        [Fact]
        public void ReachCountsHeroesWithAnAttackOnHisRank()
        {
            var lib = Board(Maa, 0, 1, 2, 3, 3);
            var l = LibrarianPlay.FindLibrarian(lib);
            // Rank 4: only Pistol from rank 2.
            Assert.Equal(1, LibrarianPlay.ReachCount(lib, null, l));
            // MAA to rank 1: GR Thrown Dagger from rank 2 and HWM Pistol from rank 3.
            Assert.Equal(2, LibrarianPlay.ReachCount(lib, LibrarianPlay.RanksAfterMove(lib, lib.Performer, 0), l));
        }

        [Fact]
        public void MaaWalksSoGrReachesAfterCategorize()
        {
            var snap = Board(Maa, 0, 1, 2, 3, 3);
            Legal(snap, "maa_move", Gr);
            Legal(snap, "maa_move", Hwm);
            Legal(snap, "maa_move", Pd);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("maa_move", r.Chosen.SkillId);
            Assert.Equal(Gr, r.Chosen.TargetGuid);
            Assert.Equal("librarian_reach", r.Chosen.Reason);
        }

        [Fact]
        public void NoWalkThatOnlyTradesOneReacherForAnother()
        {
            // GR on rank 1: every GR move pushes HWM off Pistol. 1 reacher either way.
            var snap = Board(Gr, 0, 1, 2, 3, 3);
            Legal(snap, "gr_move", Hwm);
            Legal(snap, "gr_move", Maa);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.NotEqual("librarian_reach", r.Chosen.Reason);
        }

        [Fact]
        public void DuelistsAdvanceThatLosesPistolRankRanksBelowPistol()
        {
            // HWM on rank 2, Librarian on rank 3: Duelist's Advance would put him on rank 1.
            var snap = Board(Hwm, 2, 1, 0, 3, 2);
            Legal(snap, "hwm_duelists_advance", Lib, 6f);
            Legal(snap, "hwm_pistol_shot_u", Lib, 4f);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("hwm_pistol_shot_u", r.Chosen.SkillId);
        }

        [Fact]
        public void DuelistsAdvanceStaysWhenTheNewRankStillReaches()
        {
            // Librarian on rank 2: Wicked Slice reaches him from rank 1, nobody loses reach.
            var snap = Board(Hwm, 2, 1, 0, 3, 1);
            Legal(snap, "hwm_duelists_advance", Lib, 6f);
            Legal(snap, "hwm_pistol_shot_u", Lib, 4f);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("hwm_duelists_advance", r.Chosen.SkillId);
        }

        [Fact]
        public void IgnitedLibrarianRaceBeatsAHeal()
        {
            var snap = Board(Pd, 2, 1, 0, 3, 0, ignited: true);
            var pd = snap.Find(Pd);
            pd.Hp = 9f;
            pd.HpPct = 9f / 35f;
            Legal(snap, "pd_battlefield_medicine_u", Pd, 0f, 10f);
            Legal(snap, "pd_noxious_blast_u", Lib, 4f);
            snap.Skills["pd_battlefield_medicine_u"] = new SkillSnap { Id = "pd_battlefield_medicine_u", IsFriendly = true, LaunchMask = 0xC, TargetMasks = new int[16], Tags = new List<string> { "heal" }, Role = new SkillRole { Heals = true } };
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("pd_noxious_blast_u", r.Chosen.SkillId);
        }

        [Fact]
        public void BookStacksDoNotHoldTheBattleOpen()
        {
            Assert.False(LibrarianPlay.HoldsBattleOpen(new TargetInfo { ClassId = "fanatic_librarian_stack_s", Living = true }));
            Assert.False(LibrarianPlay.HoldsBattleOpen(new TargetInfo { ClassId = "coven_mushroom", BattleComplete = true, Living = true }));
            Assert.True(LibrarianPlay.HoldsBattleOpen(new TargetInfo { ClassId = "fanatic_librarian", Living = true }));
        }

        [Fact]
        public void LibrarianKillWithStacksLeftIsAWin()
        {
            // enemies_left no longer counts m_IsBattleComplete actors.
            Assert.Equal("win", OutcomeLog.Outcome(false, 4, 4, 0, false));
            Assert.Equal("loss", OutcomeLog.Outcome(false, 4, 0, 1, false));
            Assert.Equal("unresolved", OutcomeLog.Outcome(false, 4, 3, 1, false));
        }
    }
}
