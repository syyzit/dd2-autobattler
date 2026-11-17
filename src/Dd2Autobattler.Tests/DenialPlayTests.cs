using System.Collections.Generic;
using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    // Shackles of Denial attack choice (denial_ev) and the walk back onto a
    // launch rank (denial_walk), notes/cited-bosses.md.
    public class DenialPlayTests
    {
        private const uint Melee = 51, Ranged = 52, Health = 50, Stress = 53;
        private const uint Gr = 1, Maa = 2, Pd = 3, Hwm = 4;

        private static TargetInfo Lock(uint g, int rank, float hp = 55f)
        {
            var cls = g == Health ? "boss_brain_lock_health" : g == Melee ? "boss_brain_lock_melee" : g == Ranged ? "boss_brain_lock_ranged" : "boss_brain_lock_stress";
            return new TargetInfo { Guid = g, ClassId = cls, Team = 1, Living = true, Hp = hp, HpMax = 55f, HpPct = hp / 55f, Rank = rank };
        }

        private static SkillSnap Skill(string id, string tag, int launchMask, bool friendly = false, bool move = false)
        {
            var masks = new int[16];
            for (var i = 0; i < masks.Length; i++)
                masks[i] = 0xF;
            var attack = !friendly && !move;
            return new SkillSnap
            {
                Id = id, Tags = new List<string> { tag }, LaunchMask = launchMask, TargetMasks = masks, IsFriendly = friendly, IsMove = move,
                Role = new SkillRole { Attacks = attack }
            };
        }

        // Ranged-tagged party (GR + HWM pistol), locks Melee r0, Ranged r1, Health r2, Stress r3.
        private static TurnSnapshot Board(uint performer, TargetInfo body)
        {
            var snap = new TurnSnapshot { PerformerGuid = performer, Performer = body, FightId = "mountain_boss_brain_2" };
            snap.Heroes.Add(body);
            snap.Enemies.Add(Lock(Melee, 0));
            snap.Enemies.Add(Lock(Ranged, 1));
            snap.Enemies.Add(Lock(Health, 2));
            snap.Enemies.Add(Lock(Stress, 3));
            foreach (var e in snap.Enemies)
                snap.Threats.Add(new EnemyThreat { Guid = e.Guid, ClassId = e.ClassId, Boss = true });
            snap.Skills["gr_thrown_dagger_u"] = Skill("gr_thrown_dagger_u", "ranged", 0xE);
            snap.Skills["gr_flashing_daggers_u"] = Skill("gr_flashing_daggers_u", "ranged", 0xE);
            snap.Skills["gr_pick_to_the_face_u"] = Skill("gr_pick_to_the_face_u", "melee", 0x7);
            snap.Skills["hwm_pistol_shot_u"] = Skill("hwm_pistol_shot_u", "ranged", 0xE);
            snap.Skills["pd_noxious_blast_u"] = Skill("pd_noxious_blast_u", "ranged", 0xE);
            snap.Skills["pd_incision"] = Skill("pd_incision", "melee", 0x7);
            return snap;
        }

        private static TargetInfo Hero(uint g, string cls, int rank, List<string> skills, float hp = 30f, float max = 35f)
        {
            return new TargetInfo { Guid = g, ClassId = cls, Team = 0, Living = true, Hp = hp, HpMax = max, HpPct = hp / max, Rank = rank, SkillIds = skills };
        }

        private static void Legal(TurnSnapshot snap, string skill, uint target, float dmg, params (uint guid, float dmg)[] hits)
        {
            var p = new PreviewScore { Ok = true, Damage = dmg, DamageValid = true, HitChance = 1f };
            p.HitGuids.Add(target);
            foreach (var h in hits)
            {
                p.Hits.Add(new PreviewHit { Guid = h.guid, Damage = h.dmg });
                if (!p.HitGuids.Contains(h.guid))
                    p.HitGuids.Add(h.guid);
            }
            snap.Legal.Add(new LegalSnap { Skill = skill, Target = target, Preview = p });
        }

        [Fact]
        public void FlashingOverThrownWhenItAlsoHitsTheFocus()
        {
            var gr = Hero(Gr, "grave_robber", 2, new List<string> { "gr_thrown_dagger_u", "gr_flashing_daggers_u", "gr_pick_to_the_face_u", "hwm_pistol_shot_u" });
            var snap = Board(Gr, gr);
            // Thrown Dagger 7 on Ranged (the head of the order, weight 4: 28) vs Flashing
            // 7 on Melee (3) + 7 on Health (2): 35. The default scorer vetoes the
            // deferred-lock click while the must-kill is reachable.
            Legal(snap, "gr_thrown_dagger_u", Ranged, 7f);
            Legal(snap, "gr_flashing_daggers_u", Melee, 14f, (Melee, 7f), (Health, 7f));
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("gr_flashing_daggers_u", r.Chosen.SkillId);
            Assert.Equal("denial_ev", r.Chosen.Reason);
        }

        [Fact]
        public void WeightsFollowTheKillOrder()
        {
            var gr = Hero(Gr, "grave_robber", 2, new List<string> { "gr_thrown_dagger_u", "hwm_pistol_shot_u" });
            var snap = Board(Gr, gr);
            snap.Reindex();
            var focus = EnemyFocus.Scan(snap);
            uint healthGuard;
            var w = DenialPlay.LockWeights(focus, snap, out healthGuard);
            Assert.Equal(4f, w[Ranged]);
            Assert.Equal(3f, w[Melee]);
            Assert.Equal(2f, w[Health]);
            Assert.Equal(1f, w[Stress]);
            Assert.Equal(Health, healthGuard);
        }

        [Fact]
        public void NoxiousBlightCountsThreeTicks()
        {
            var pd = Hero(Pd, "plague_doctor", 3, new List<string> { "pd_noxious_blast_u", "pd_incision", "gr_thrown_dagger_u", "hwm_pistol_shot_u" });
            var snap = Board(Pd, pd);
            // Incision 4 on Ranged (no DoT here) vs Noxious 3.5 + blight 5 x 3 on Melee (res 0.1).
            Legal(snap, "pd_incision", Ranged, 4f);
            Legal(snap, "pd_noxious_blast_u", Melee, 3.5f);
            snap.Legal[1].Preview.ApplyBlight = 5f;
            snap.Legal[1].Preview.ResistOk = true;
            snap.Legal[1].Preview.ResistBlight = 0.1f;
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("pd_noxious_blast_u", r.Chosen.SkillId);
        }

        [Fact]
        public void NeverKillsHealthFirst()
        {
            var gr = Hero(Gr, "grave_robber", 2, new List<string> { "gr_thrown_dagger_u", "gr_flashing_daggers_u", "hwm_pistol_shot_u" });
            var snap = Board(Gr, gr);
            snap.Enemies[2] = Lock(Health, 2, 6f);
            // Flashing would finish Health (6) while all four locks live: its death heal would fire.
            Legal(snap, "gr_thrown_dagger_u", Ranged, 7f);
            Legal(snap, "gr_flashing_daggers_u", Ranged, 14f, (Ranged, 7f), (Health, 7f));
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("gr_thrown_dagger_u", r.Chosen.SkillId);
        }

        // Melee already dead (logs: a dead lock leaves the enemy list):
        // Ranged r0, Health r1, Stress r2. Order Ranged, Health, Stress.
        private static TurnSnapshot ThreeLocks(uint performer, TargetInfo body, float ranged, float health, float stress)
        {
            var snap = Board(performer, body);
            snap.Enemies.Clear();
            snap.Threats.Clear();
            snap.Enemies.Add(Lock(Ranged, 0, ranged));
            snap.Enemies.Add(Lock(Health, 1, health));
            snap.Enemies.Add(Lock(Stress, 2, stress));
            foreach (var e in snap.Enemies)
                snap.Threats.Add(new EnemyThreat { Guid = e.Guid, ClassId = e.ClassId, Boss = true });
            return snap;
        }

        private static TargetInfo RangedGr()
        {
            return Hero(Gr, "grave_robber", 2, new List<string> { "gr_thrown_dagger_u", "gr_flashing_daggers_u", "gr_pick_to_the_face_u", "hwm_pistol_shot_u" });
        }

        [Fact]
        public void HealthKillThatHealsAWoundedLockIsNotTaken()
        {
            // Logged brain_2 r11: Melee died first, then Health;
            // its death heal still put Ranged 8 -> 26 (CSV: the heal fires on
            // every Health death). Ranged is out of reach here; Pick would
            // finish Health (10) and give Ranged (20/55) 18 back.
            var snap = ThreeLocks(Gr, RangedGr(), 20f, 10f, 55f);
            Legal(snap, "gr_pick_to_the_face_u", Health, 12f);
            Legal(snap, "gr_thrown_dagger_u", Stress, 7f);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.False(r.Chosen.SkillId == "gr_pick_to_the_face_u" && r.Chosen.TargetGuid == Health);
        }

        [Fact]
        public void HealthKillWithEveryOtherLockWholeStands()
        {
            var snap = ThreeLocks(Gr, RangedGr(), 55f, 10f, 55f);
            Legal(snap, "gr_pick_to_the_face_u", Health, 12f);
            Legal(snap, "gr_thrown_dagger_u", Stress, 7f);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("gr_pick_to_the_face_u", r.Chosen.SkillId);
            Assert.Equal(Health, r.Chosen.TargetGuid);
        }

        [Fact]
        public void SplashOnALockHealthWillHealIsWorthNothing()
        {
            // Logged brain_2: Flashing splashed Stress 55 -> 38
            // before Health died; the heal put it back to 55.
            var snap = ThreeLocks(Gr, RangedGr(), 30f, 55f, 55f);
            Legal(snap, "gr_thrown_dagger_u", Ranged, 8f);
            Legal(snap, "gr_flashing_daggers_u", Ranged, 12f, (Ranged, 6f), (Stress, 6f));
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("gr_thrown_dagger_u", r.Chosen.SkillId);

            var focus = EnemyFocus.Scan(snap);
            var plan = DenialPlay.Plan(focus, snap);
            Assert.Contains(Stress, plan.HealedLater);
            var flashing = r.Candidates.Find(c => c.SkillId == "gr_flashing_daggers_u");
            // Ranged weight 3 x 6; Stress 55 -> 49 is inside the 33% band: 0.
            Assert.Equal(18f, (double)DenialPlay.Value(flashing, snap, plan), 2);

            // Past the band (Stress 30/55, 25 missing) the splash counts.
            var deep = ThreeLocks(Gr, RangedGr(), 30f, 55f, 30f);
            Legal(deep, "gr_thrown_dagger_u", Ranged, 8f);
            Legal(deep, "gr_flashing_daggers_u", Ranged, 12f, (Ranged, 6f), (Stress, 6f));
            deep.Reindex();
            var r2 = TurnDecider.DecideSnapshot(deep);
            var plan2 = DenialPlay.Plan(EnemyFocus.Scan(deep), deep);
            var flashing2 = r2.Candidates.Find(c => c.SkillId == "gr_flashing_daggers_u");
            Assert.Equal(24f, (double)DenialPlay.Value(flashing2, deep, plan2), 2);
        }

        private static void DotItem(TurnSnapshot snap, string id, float bleed, int qty, params uint[] targets)
        {
            var def = new SkillSnap { Id = id, IsItemSkill = true, IsFree = true, Role = new SkillRole { Attacks = true, Bleed = true } };
            def.Tags.Add(id);
            def.Tags.Add("combat_item");
            snap.Skills[id] = def;
            snap.ItemQty[id] = qty;
            foreach (var t in targets)
            {
                var p = new PreviewScore { Ok = true, HitChance = 1f, ApplyBleed = bleed, ResistOk = true, ResistBleed = 0.1f };
                if (id == "crows_feet")
                {
                    foreach (var e in snap.Enemies)
                    {
                        p.HitGuids.Add(e.Guid);
                        p.Hits.Add(new PreviewHit { Guid = e.Guid });
                    }
                }
                else
                {
                    p.HitGuids.Add(t);
                    p.Hits.Add(new PreviewHit { Guid = t });
                }
                snap.Legal.Add(new LegalSnap { Skill = id, Target = t, Preview = p });
            }
        }

        [Fact]
        public void SpikedBallGoesOnTheHeadOfTheOrder()
        {
            // CSV spiked_ball: free action, skill_dot_very_large_bleed 5 x 3.
            // Logs: never used in 30 attempts (scored 6-10 < 18 on direct damage).
            var gr = RangedGr();
            gr.HeldItems = new List<string> { "spiked_ball" };
            var snap = Board(Gr, gr);
            Legal(snap, "gr_thrown_dagger_u", Ranged, 7f);
            DotItem(snap, "spiked_ball", 5f, 1, Melee, Ranged, Health, Stress);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("spiked_ball", r.Chosen.SkillId);
            Assert.Equal(Ranged, r.Chosen.TargetGuid);
            Assert.Equal("denial_dot_item", r.Chosen.Reason);
        }

        [Fact]
        public void CrowsFeetBleedsEveryLock()
        {
            var gr = RangedGr();
            gr.HeldItems = new List<string> { "crows_feet" };
            var snap = Board(Gr, gr);
            Legal(snap, "gr_thrown_dagger_u", Ranged, 7f);
            DotItem(snap, "crows_feet", 1f, 2, Melee, Ranged);
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("crows_feet", r.Chosen.SkillId);
            Assert.Equal("denial_dot_item", r.Chosen.Reason);
        }

        [Fact]
        public void DotItemNotOnANearlyDeadLockNorOutsideDenial()
        {
            var gr = RangedGr();
            gr.HeldItems = new List<string> { "bear_trap" };
            var snap = ThreeLocks(Gr, gr, 3f, 55f, 55f);
            Legal(snap, "gr_thrown_dagger_u", Ranged, 7f);
            // Bear Trap 3 x 3 on Ranged at 3 HP: 3 usable, under MinItemDot.
            DotItem(snap, "bear_trap", 3f, 1, Ranged);
            snap.Reindex();
            Assert.NotEqual("bear_trap", TurnDecider.DecideSnapshot(snap).Chosen.SkillId);

            var other = new TurnSnapshot { PerformerGuid = Gr, Performer = gr };
            other.Heroes.Add(gr);
            other.Enemies.Add(new TargetInfo { Guid = 60, ClassId = "bandit_cutthroat", Team = 1, Living = true, Hp = 30f, HpMax = 30f, HpPct = 1f, Rank = 1 });
            other.Skills["gr_thrown_dagger_u"] = Skill("gr_thrown_dagger_u", "ranged", 0xE);
            Legal(other, "gr_thrown_dagger_u", 60, 7f);
            DotItem(other, "spiked_ball", 5f, 1, 60);
            other.Reindex();
            Assert.NotEqual("denial_dot_item", TurnDecider.DecideSnapshot(other).Chosen.Reason);
        }

        [Fact]
        public void NotOutsideDenial()
        {
            var gr = Hero(Gr, "grave_robber", 2, new List<string> { "gr_thrown_dagger_u", "gr_flashing_daggers_u" });
            var snap = new TurnSnapshot { PerformerGuid = Gr, Performer = gr };
            snap.Heroes.Add(gr);
            snap.Enemies.Add(new TargetInfo { Guid = 60, ClassId = "bandit_cutthroat", Team = 1, Living = true, Hp = 30f, HpMax = 30f, HpPct = 1f, Rank = 1 });
            snap.Enemies.Add(new TargetInfo { Guid = 61, ClassId = "bandit_cutthroat", Team = 1, Living = true, Hp = 30f, HpMax = 30f, HpPct = 1f, Rank = 2 });
            snap.Skills["gr_thrown_dagger_u"] = Skill("gr_thrown_dagger_u", "ranged", 0xE);
            snap.Skills["gr_flashing_daggers_u"] = Skill("gr_flashing_daggers_u", "ranged", 0xE);
            Legal(snap, "gr_thrown_dagger_u", 60, 7f);
            Legal(snap, "gr_flashing_daggers_u", 60, 10f, (60, 5f), (61, 5f));
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.NotEqual("denial_ev", r.Chosen.Reason);
        }

        [Fact]
        public void CursedOrMovingSkillsAreNotTakenForDamage()
        {
            var hwm = Hero(Hwm, "highwayman", 2, new List<string> { "hwm_pistol_shot_u", "hwm_duelists_advance_u", "gr_thrown_dagger_u" });
            var snap = Board(Hwm, hwm);
            snap.Skills["hwm_duelists_advance_u"] = Skill("hwm_duelists_advance_u", "melee", 0xE);
            Legal(snap, "hwm_pistol_shot_u", Ranged, 7f);
            // Duelist's Advance walks the Highwayman forward; Flashing is cursed this turn.
            Legal(snap, "hwm_duelists_advance_u", Melee, 14f);
            Legal(snap, "gr_flashing_daggers_u", Ranged, 14f, (Ranged, 7f), (Health, 7f));
            snap.CursedSkills.Add("gr_flashing_daggers_u");
            snap.Reindex();
            var r = TurnDecider.DecideSnapshot(snap);
            Assert.Equal("hwm_pistol_shot_u", r.Chosen.SkillId);
        }

        private static TurnSnapshot MaaAtBack(bool partnerLaunchesFromBack, bool deathsDoor = false)
        {
            var maa = Hero(Maa, "man_at_arms", 3, new List<string> { "maa_crush_u", "maa_rampart_u", "maa_bolster" }, deathsDoor ? 0f : 30f, 55f);
            maa.DeathsDoor = deathsDoor;
            var snap = Board(Maa, maa);
            var pd = Hero(Pd, "plague_doctor", 2, new List<string> { partnerLaunchesFromBack ? "pd_noxious_blast_u" : "pd_incision" }, 30f, 35f);
            pd.Stress = 2f;
            snap.Heroes.Add(pd);
            // The Highwayman already hits the focus lock, so the generic reach walk stays off.
            var hwm = Hero(Hwm, "highwayman", 0, new List<string> { "hwm_wicked_slice_u" }, 40f, 42f);
            snap.Heroes.Add(hwm);
            snap.Skills["hwm_wicked_slice_u"] = Skill("hwm_wicked_slice_u", "melee", 0x7);
            snap.Skills["maa_crush_u"] = Skill("maa_crush_u", "melee", 0x3);
            snap.Skills["maa_rampart_u"] = Skill("maa_rampart_u", "melee", 0x7);
            snap.Skills["maa_bolster"] = Skill("maa_bolster", "stress_heal", 0xF, friendly: true);
            snap.Skills["maa_move"] = Skill("maa_move", "move", 0xF, move: true);
            snap.Legal.Add(new LegalSnap { Skill = "maa_bolster", Target = Pd, Preview = new PreviewScore { Ok = true } });
            snap.Legal.Add(new LegalSnap { Skill = "maa_move", Target = Pd, Preview = new PreviewScore { Ok = true } });
            snap.Reindex();
            return snap;
        }

        [Fact]
        public void ManAtArmsAtTheBackWalksTowardALaunchRank()
        {
            var r = TurnDecider.DecideSnapshot(MaaAtBack(true));
            Assert.Equal("maa_move", r.Chosen.SkillId);
            Assert.Equal("denial_walk", r.Chosen.Reason);
            var snap = MaaAtBack(true);
            Assert.Equal(1, DenialPlay.LaunchDistance(snap.Performer, 3, snap));
            Assert.Equal(0, DenialPlay.LaunchDistance(snap.Performer, 2, snap));
        }

        [Fact]
        public void NoWalkThatStrandsThePartnerOrOnDeathsDoor()
        {
            Assert.NotEqual("maa_move", TurnDecider.DecideSnapshot(MaaAtBack(false)).Chosen.SkillId);
            Assert.NotEqual("denial_walk", TurnDecider.DecideSnapshot(MaaAtBack(true, deathsDoor: true)).Chosen.Reason);
        }
    }
}
