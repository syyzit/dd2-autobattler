using System.Collections.Generic;
using Dd2Autobattler.Combat;
using Xunit;
using ScoredAction = Dd2Autobattler.Combat.TurnDecider.ScoredAction;

namespace Dd2Autobattler.Tests
{
    // End-of-fight recovery (notes/recovery.md): idle turns on the last safe
    // enemy become heals / DoT strips / stress heals; nothing delays the kill.
    public class RecoveryTests
    {
        private const uint Maa = 1, Hwm = 2, Gr = 3, Pd = 4, Foe = 10, Foe2 = 11;

        private static TargetInfo Hero(uint guid, string cls, float hp, float max, float stress)
        {
            return new TargetInfo { Guid = guid, ClassId = cls, Team = 0, Living = true, Hp = hp, HpMax = max, HpPct = hp / max, Stress = stress };
        }

        private static TargetInfo Enemy(uint guid, string cls, float hp = 10f)
        {
            return new TargetInfo { Guid = guid, ClassId = cls, Team = 1, Living = true, Hp = hp, HpMax = 15f, HpPct = hp / 15f };
        }

        private static TurnSnapshot Snap(params TargetInfo[] enemies)
        {
            var snap = new TurnSnapshot();
            snap.Heroes.Add(Hero(Maa, "man_at_arms", 30f, 40f, 6f));
            snap.Heroes.Add(Hero(Hwm, "highwayman", 30f, 35f, 2f));
            snap.Heroes.Add(Hero(Gr, "grave_robber", 25f, 29f, 1f));
            snap.Heroes.Add(Hero(Pd, "plague_doctor", 25f, 30f, 6f));
            foreach (var e in enemies)
                snap.Enemies.Add(e);
            snap.PerformerGuid = Maa;
            snap.Performer = snap.Heroes[0];
            return snap;
        }

        private static ScoredAction Act(string skill, SkillKind kind, TargetInfo target, float score, float dmg = 0f, float heal = 0f, bool kills = false)
        {
            return new ScoredAction
            {
                SkillId = skill,
                TargetGuid = target.Guid,
                Kind = kind,
                EnemyTarget = target.Team == 1,
                Target = target,
                Preview = new PreviewScore { Ok = true, Damage = dmg, Heal = heal, Kills = kills },
                Score = score
            };
        }

        private static EnemyFocus Focus(TargetInfo enemy, System.Action<EnemyThreat> edit = null)
        {
            var f = new EnemyFocus();
            var t = new EnemyThreat { Guid = enemy.Guid, ClassId = enemy.ClassId };
            if (edit != null)
                edit(t);
            f.Enemies.Add(t);
            return f;
        }

        private static ScoredAction Decide(TurnSnapshot snap, ScoredAction picked, List<ScoredAction> all, EnemyFocus focus, bool enabled = true, int living = 1)
        {
            return TurnDecider.RecoverOverIdle(picked, all, focus, snap, snap.Performer, living, living, enabled);
        }

        [Fact]
        public void BolsterReplacesLosingGuardOnSafeLastEnemy()
        {
            var foe = Enemy(Foe, "lost_battalion_arbalist");
            var snap = Snap(foe);
            var guard = Act("maa_defender", SkillKind.Support, snap.Find(Pd), -35f);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -65f);
            var crush = Act("maa_crush", SkillKind.Attack, foe, 20f, dmg: 6f);
            var pick = Decide(snap, guard, new List<ScoredAction> { crush, guard, bolster }, Focus(foe));
            Assert.Same(bolster, pick);
            Assert.Equal("recover", pick.NoteReason);
        }

        [Fact]
        public void SwitchOffKeepsThePick()
        {
            var foe = Enemy(Foe, "lost_battalion_arbalist");
            var snap = Snap(foe);
            var guard = Act("maa_defender", SkillKind.Support, snap.Find(Pd), -35f);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -65f);
            Assert.Same(guard, Decide(snap, guard, new List<ScoredAction> { guard, bolster }, Focus(foe), enabled: false));
        }

        [Fact]
        public void NeverReplacesADamagingHitOrAKill()
        {
            var foe = Enemy(Foe, "lost_battalion_arbalist");
            var snap = Snap(foe);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -65f);
            var chip = Act("maa_crush", SkillKind.Attack, foe, 20f, dmg: 6f);
            var kill = Act("maa_crush", SkillKind.Attack, foe, 90f, dmg: 12f, kills: true);
            Assert.Same(chip, Decide(snap, chip, new List<ScoredAction> { chip, bolster }, Focus(foe)));
            Assert.Same(kill, Decide(snap, kill, new List<ScoredAction> { kill, bolster }, Focus(foe)));
        }

        [Fact]
        public void TwoLivingEnemiesKeepThePick()
        {
            var foe = Enemy(Foe, "lost_battalion_arbalist");
            var snap = Snap(foe, Enemy(Foe2, "lost_battalion_foot_soldier"));
            var guard = Act("maa_defender", SkillKind.Support, snap.Find(Pd), -35f);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -65f);
            Assert.Same(guard, Decide(snap, guard, new List<ScoredAction> { guard, bolster }, Focus(foe), living: 2));
            // Counts say one, the board says two: still off.
            Assert.Same(guard, Decide(snap, guard, new List<ScoredAction> { guard, bolster }, Focus(foe)));
        }

        [Fact]
        public void UnsafeLastEnemyKeepsThePick()
        {
            var flags = new System.Action<EnemyThreat>[]
            {
                t => t.Boss = true, t => t.Summons = true, t => t.Resurrects = true,
                t => t.MustKillFirst = true, t => t.Commander = true
            };
            foreach (var edit in flags)
            {
                var foe = Enemy(Foe, "some_enemy");
                var snap = Snap(foe);
                var guard = Act("maa_defender", SkillKind.Support, snap.Find(Pd), -35f);
                var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -65f);
                Assert.Same(guard, Decide(snap, guard, new List<ScoredAction> { guard, bolster }, Focus(foe, edit)));
            }
            var ignited = Enemy(Foe, "fanatic_blind_shaman_ignited");
            var s2 = Snap(ignited);
            var g2 = Act("maa_defender", SkillKind.Support, s2.Find(Pd), -35f);
            var b2 = Act("maa_bolster", SkillKind.Support, s2.Find(Pd), -65f);
            Assert.Same(g2, Decide(s2, g2, new List<ScoredAction> { g2, b2 }, Focus(ignited)));
            Assert.False(TurnDecider.IsSafeLastEnemy(new TargetInfo { Guid = Foe, Living = true, Healthless = true }, null));
        }

        [Fact]
        public void DeathsDoorLeavesItToTheCrisisGates()
        {
            var foe = Enemy(Foe, "lost_battalion_arbalist");
            var snap = Snap(foe);
            snap.Find(Hwm).DeathsDoor = true;
            var guard = Act("maa_defender", SkillKind.Support, snap.Find(Hwm), -35f);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -65f);
            Assert.Same(guard, Decide(snap, guard, new List<ScoredAction> { guard, bolster }, Focus(foe)));
        }

        [Fact]
        public void LowAllyKeepsTheGuardButNotAPass()
        {
            var foe = Enemy(Foe, "lost_battalion_arbalist");
            var snap = Snap(foe);
            var low = snap.Find(Hwm);
            low.Hp = 7f; low.HpPct = 0.2f;
            var guard = Act("maa_defender", SkillKind.Support, low, -35f);
            var pass = Act("pass_stress", SkillKind.Pass, snap.Find(Maa), -50f);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Pd), -145f);
            var all = new List<ScoredAction> { guard, pass, bolster };
            Assert.Same(guard, Decide(snap, guard, all, Focus(foe)));
            Assert.Same(bolster, Decide(snap, pass, all, Focus(foe)));
        }

        [Fact]
        public void PassHealIsNotIdle()
        {
            Assert.False(TurnDecider.IsRecoveryIdle(Act("pass_heal", SkillKind.Pass, Hero(Maa, "man_at_arms", 8f, 40f, 3f), -10f, heal: 8f)));
            Assert.True(TurnDecider.IsRecoveryIdle(Act("pass_stress", SkillKind.Pass, Hero(Maa, "man_at_arms", 30f, 40f, 3f), -50f)));
            Assert.True(TurnDecider.IsRecoveryIdle(Act("pd_blinding_gas", SkillKind.Attack, Enemy(Foe, "x"), 14.5f)));
            Assert.False(TurnDecider.IsRecoveryIdle(Act("maa_retribution", SkillKind.Support, Hero(Maa, "man_at_arms", 30f, 40f, 3f), 12f)));
            var cited = Act("maa_bolster", SkillKind.Support, Hero(Pd, "plague_doctor", 20f, 30f, 9f), -10f);
            cited.NoteReason = "meltdown_guard";
            Assert.False(TurnDecider.IsRecoveryIdle(cited));
        }

        [Fact]
        public void BolsterValueFollowsTheCsvThresholds()
        {
            var maa = Hero(Maa, "man_at_arms", 30f, 40f, 4f);
            var calm = Act("maa_bolster", SkillKind.Support, Hero(Pd, "plague_doctor", 25f, 30f, 4f), -65f);
            Assert.Equal(0f, TurnDecider.RecoveryValue(calm, maa, false));
            var stressed = Act("maa_bolster", SkillKind.Support, Hero(Pd, "plague_doctor", 25f, 30f, 5f), -65f);
            Assert.Equal(TurnDecider.RecoverStressHp, TurnDecider.RecoveryValue(stressed, maa, false));
            maa.Stress = 6f;
            var upgraded = Act("maa_bolster_u", SkillKind.Support, Hero(Pd, "plague_doctor", 25f, 30f, 6f), -65f);
            Assert.Equal(4f * TurnDecider.RecoverStressHp, TurnDecider.RecoveryValue(upgraded, maa, false));
            Assert.Equal(0f, TurnDecider.RecoveryValue(upgraded, maa, true));
        }

        [Fact]
        public void MedicineHealsOrStripsDots()
        {
            var pd = Hero(Pd, "plague_doctor", 25f, 30f, 2f);
            var hurt = Hero(Maa, "man_at_arms", 16f, 40f, 2f);
            var heal = Act("pd_battlefield_medicine_u", SkillKind.Heal, hurt, 30f, heal: 10f);
            Assert.Equal(10f, TurnDecider.RecoveryValue(heal, pd, false));
            var bleeding = Hero(Hwm, "highwayman", 30f, 35f, 2f);
            bleeding.BleedDot = 2f; bleeding.NextDot = 2f;
            var strip = Act("pd_battlefield_medicine_u", SkillKind.Heal, bleeding, -20f);
            Assert.Equal(4f, TurnDecider.RecoveryValue(strip, pd, false));
            var fine = Act("pd_battlefield_medicine_u", SkillKind.Heal, Hero(Gr, "grave_robber", 25f, 29f, 1f), -20f);
            Assert.Equal(0f, TurnDecider.RecoveryValue(fine, pd, false));
        }

        [Fact]
        public void BiggerRestoreWins()
        {
            var foe = Enemy(Foe, "lost_battalion_arbalist");
            var snap = Snap(foe);
            snap.PerformerGuid = Pd;
            snap.Performer = snap.Find(Pd);
            var hurt = snap.Find(Maa);
            hurt.Hp = 16f; hurt.HpPct = 0.4f;
            var gas = Act("pd_blinding_gas", SkillKind.Attack, foe, 14.5f);
            var small = Act("pd_battlefield_medicine_u", SkillKind.Heal, snap.Find(Hwm), -20f, heal: 3f);
            var big = Act("pd_battlefield_medicine_u", SkillKind.Heal, hurt, 60f, heal: 10f);
            var pick = Decide(snap, gas, new List<ScoredAction> { gas, small, big }, Focus(foe));
            Assert.Same(big, pick);
        }
    }
}
