using System.Collections.Generic;
using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    // Shackles of Denial kill order (notes/cited-bosses.md): Health's death heal
    // only fires on the first lock death (CSV performer_has_0_count +
    // count_plus_1), so a ranged-denied party kills Ranged first and Health
    // second-to-last; each hero focuses the first lock in that order it can
    // damage from here.
    public class DenialLocksTests
    {
        private const uint Health = 50, Melee = 51, Ranged = 52, Stress = 53;
        private const uint Hero = 1;

        private static TurnSnapshot Snap(bool rangedParty, uint[] locks, uint[] reach)
        {
            var snap = new TurnSnapshot { PerformerGuid = Hero };
            var hero = new TargetInfo { Guid = Hero, ClassId = "grave_robber", Team = 0, Living = true, Hp = 30f, HpMax = 35f, HpPct = 30f / 35f };
            hero.SkillIds = rangedParty
                ? new List<string> { "gr_thrown_dagger_u", "gr_poison_dart", "gr_flashing_daggers_u", "gr_pick_to_the_face_u" }
                : new List<string> { "gr_pick_to_the_face_u", "hwm_wicked_slice_u", "maa_crush_u", "gr_thrown_dagger_u" };
            snap.Heroes.Add(hero);
            snap.Performer = hero;
            AddSkill(snap, "gr_thrown_dagger_u", "ranged");
            AddSkill(snap, "gr_poison_dart", "ranged");
            AddSkill(snap, "gr_flashing_daggers_u", "ranged");
            AddSkill(snap, "gr_pick_to_the_face_u", "melee");
            AddSkill(snap, "hwm_wicked_slice_u", "melee");
            AddSkill(snap, "maa_crush_u", "melee");
            for (var i = 0; i < locks.Length; i++)
            {
                var g = locks[i];
                var cls = g == Health ? "boss_brain_lock_health" : g == Melee ? "boss_brain_lock_melee" : g == Ranged ? "boss_brain_lock_ranged" : "boss_brain_lock_stress";
                snap.Enemies.Add(new TargetInfo { Guid = g, ClassId = cls, Team = 1, Living = true, Hp = 55f, HpMax = 55f, HpPct = 1f, Rank = i });
                snap.Threats.Add(new EnemyThreat { Guid = g, ClassId = cls, Boss = true });
            }
            for (var i = 0; i < reach.Length; i++)
                snap.Legal.Add(new LegalSnap { Skill = "gr_pick_to_the_face_u", Target = reach[i], Preview = new PreviewScore { Ok = true, Damage = 7f } });
            // A 0-damage mark on everything is not reach.
            for (var i = 0; i < locks.Length; i++)
                snap.Legal.Add(new LegalSnap { Skill = "hwm_tracking_shot", Target = locks[i], Preview = new PreviewScore { Ok = true, Damage = 0f } });
            snap.Reindex();
            return snap;
        }

        private static void AddSkill(TurnSnapshot snap, string id, string tag)
        {
            snap.Skills[id] = new SkillSnap { Id = id, Tags = new List<string> { tag } };
        }

        private static uint[] All = { Melee, Ranged, Health, Stress };

        [Fact]
        public void RangedPartyKillsRangedFirstAndHealthSecondToLast()
        {
            var snap = Snap(true, All, All);
            var f = EnemyFocus.Scan(snap);
            Assert.True(f.DenialLocks);
            Assert.True(f.IsMustKillFirst(Ranged));
            Assert.False(f.IsMustKillFirst(Health));
            Assert.True(f.IsOpenWhenUnreached(Health));

            var locks = new List<EnemyThreat>();
            foreach (var t in snap.Threats)
                locks.Add(t);
            var order = EnemyFocus.DenialKillOrder(locks, snap);
            Assert.Equal(new[] { Ranged, Melee, Health, Stress }, order.ConvertAll(t => t.Guid).ToArray());
        }

        [Fact]
        public void MeleePartyKeepsHealthFirstUntilALockDies()
        {
            var f = EnemyFocus.Scan(Snap(false, All, All));
            Assert.True(f.IsMustKillFirst(Health));

            // Ranged already dead: the heal is off, Health is second-to-last.
            var three = Snap(false, new[] { Melee, Health, Stress }, new[] { Melee, Health, Stress });
            var g = EnemyFocus.Scan(three);
            Assert.True(g.IsMustKillFirst(Melee));
            Assert.False(g.IsMustKillFirst(Health));
        }

        [Fact]
        public void HeroThatCannotDamageTheFocusHitsTheNextLock()
        {
            // Ranged lock out of reach (lock token / rank): Melee is this hero's must-kill.
            var f = EnemyFocus.Scan(Snap(true, All, new[] { Melee, Health }));
            Assert.True(f.IsMustKillFirst(Melee));
            Assert.False(f.IsMustKillFirst(Ranged));
            Assert.True(f.IsDeferred(Ranged));

            // Only Health reachable: hit Health rather than idle.
            var h = EnemyFocus.Scan(Snap(true, All, new[] { Health }));
            Assert.True(h.IsMustKillFirst(Health));

            // Nothing reachable: the order's head stays the must-kill (rank walks key off it).
            var none = EnemyFocus.Scan(Snap(true, All, new uint[0]));
            Assert.True(none.IsMustKillFirst(Ranged));
        }
    }
}
