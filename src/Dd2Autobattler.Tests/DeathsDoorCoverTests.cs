using System.Collections.Generic;
using Dd2Autobattler.Combat;
using Xunit;
using ScoredAction = Dd2Autobattler.Combat.TurnDecider.ScoredAction;

namespace Dd2Autobattler.Tests
{
    // Death-centred fixes (from logged non-lair fights):
    // Defender on a Death's Door / <=25% ally over a non-kill swing (cover_guard),
    // the Riposte return on the performer (preview Hit on the performer) on Death's
    // Door, and the awkward-last-enemy setup gate during a party crisis.
    public class DeathsDoorCoverTests
    {
        private const uint Maa = 1, Hwm = 2, Gr = 3, Pd = 4, Foe = 10, Foe2 = 11;

        private static TargetInfo Hero(uint guid, string cls, int rank, float hp, float max)
        {
            return new TargetInfo { Guid = guid, ClassId = cls, Team = 0, Rank = rank, Living = true, Hp = hp, HpMax = max, HpPct = hp / max, Tokens = new List<string>() };
        }

        private static TargetInfo Enemy(uint guid, string cls, int rank, float hp = 15f)
        {
            return new TargetInfo { Guid = guid, ClassId = cls, Team = 1, Rank = rank, Living = true, Hp = hp, HpMax = 20f, HpPct = hp / 20f, Tokens = new List<string>() };
        }

        private static TurnSnapshot Snap(float maaHp, float hwmHp, bool hwmDoor = false)
        {
            var snap = new TurnSnapshot();
            snap.Heroes.Add(Hero(Maa, "man_at_arms", 1, maaHp, 60f));
            var hwm = Hero(Hwm, "highwayman", 0, hwmHp, 39f);
            hwm.DeathsDoor = hwmDoor;
            snap.Heroes.Add(hwm);
            snap.Heroes.Add(Hero(Gr, "grave_robber", 3, 21f, 35f));
            snap.Heroes.Add(Hero(Pd, "plague_doctor", 2, 20f, 36f));
            snap.Enemies.Add(Enemy(Foe, "shared_carrion_eater_mutated", 0, 21f));
            snap.Enemies.Add(Enemy(Foe2, "shared_carrion_eater", 1, 8f));
            snap.PerformerGuid = Maa;
            snap.Performer = snap.Find(Maa);
            return snap;
        }

        private static ScoredAction Act(string skill, SkillKind kind, TargetInfo target, float score, float dmg = 0f, bool kills = false)
        {
            var p = new PreviewScore { Ok = true, Damage = dmg, Kills = kills };
            if (skill.StartsWith("maa_defender"))
                p.ApplyTarget.Add("guard");
            return new ScoredAction
            {
                SkillId = skill,
                TargetGuid = target.Guid,
                Kind = kind,
                EnemyTarget = target.Team == 1,
                Target = target,
                Preview = p,
                Score = score
            };
        }

        private static List<ScoredAction> Kit(TurnSnapshot snap, out ScoredAction crush, out ScoredAction guardHwm, out ScoredAction guardPd)
        {
            crush = Act("maa_crush_u", SkillKind.Attack, snap.Find(Foe), 78f, dmg: 4f);
            guardHwm = Act("maa_defender", SkillKind.Support, snap.Find(Hwm), -66f);
            guardPd = Act("maa_defender", SkillKind.Support, snap.Find(Pd), -68f);
            var bolster = Act("maa_bolster", SkillKind.Support, snap.Find(Hwm), -54f);
            return new List<ScoredAction> { crush, guardHwm, guardPd, bolster };
        }

        // Logged spitting_distance r10: Highwayman 9/39 at rank 0,
        // MAA 29/60 crushed (focus_boss 78) over Defender (-66); two bites killed him.
        [Fact]
        public void HealthyMaaGuardsAllyAtQuarterHpInsteadOfSwinging()
        {
            var snap = Snap(29f, 9f);
            ScoredAction crush, guardHwm, guardPd;
            var all = Kit(snap, out crush, out guardHwm, out guardPd);
            var pick = TurnDecider.CoverGuardOverSwing(crush, all, snap, snap.Performer, "attack");
            Assert.Same(guardHwm, pick);
            Assert.Equal("cover_guard", pick.NoteReason);
        }

        [Fact]
        public void DeathsDoorAllyComesFirst()
        {
            var snap = Snap(40f, 8f);
            var pd = snap.Find(Pd);
            pd.Hp = 0f; pd.HpPct = 0f; pd.DeathsDoor = true;
            ScoredAction crush, guardHwm, guardPd;
            var all = Kit(snap, out crush, out guardHwm, out guardPd);
            Assert.Same(guardPd, TurnDecider.CoverGuardOverSwing(crush, all, snap, snap.Performer, "attack"));
        }

        [Fact]
        public void NoCoverAboveQuarterHpOrWhenAlreadyGuarded()
        {
            var snap = Snap(40f, 12f); // 31%: the crisis heal band, not a guard
            ScoredAction crush, guardHwm, guardPd;
            var all = Kit(snap, out crush, out guardHwm, out guardPd);
            Assert.Same(crush, TurnDecider.CoverGuardOverSwing(crush, all, snap, snap.Performer, "attack"));

            var door = Snap(40f, 0f, hwmDoor: true);
            door.Find(Hwm).Tokens.Add("guard");
            all = Kit(door, out crush, out guardHwm, out guardPd);
            Assert.Same(crush, TurnDecider.CoverGuardOverSwing(crush, all, door, door.Performer, "attack"));
        }

        [Fact]
        public void GuardianInCrisisDoesNotCover()
        {
            // 35% or lower, Death's Door, or dying to his own DoT: redirected hits
            // would land on a hero who is in trouble himself.
            foreach (var hp in new[] { 21f, 0f })
            {
                var snap = Snap(hp, 0f, hwmDoor: true);
                snap.Performer.DeathsDoor = hp <= 0f;
                ScoredAction crush, guardHwm, guardPd;
                var all = Kit(snap, out crush, out guardHwm, out guardPd);
                Assert.Same(crush, TurnDecider.CoverGuardOverSwing(crush, all, snap, snap.Performer, "attack"));
            }
            var dot = Snap(30f, 0f, hwmDoor: true);
            dot.Performer.NextDot = 31f;
            ScoredAction c2, g2, p2;
            var kit = Kit(dot, out c2, out g2, out p2);
            Assert.Same(c2, TurnDecider.CoverGuardOverSwing(c2, kit, dot, dot.Performer, "attack"));
        }

        [Fact]
        public void KillsHealsTauntAndCitedGatesStand()
        {
            var snap = Snap(40f, 0f, hwmDoor: true);
            ScoredAction crush, guardHwm, guardPd;
            var all = Kit(snap, out crush, out guardHwm, out guardPd);

            var kill = Act("maa_crush_u", SkillKind.Attack, snap.Find(Foe2), 120f, dmg: 9f, kills: true);
            all.Add(kill);
            Assert.Same(kill, TurnDecider.CoverGuardOverSwing(kill, all, snap, snap.Performer, "attack"));

            var taunt = Act("maa_retribution", SkillKind.Support, snap.Performer, 24f);
            all.Add(taunt);
            Assert.Same(taunt, TurnDecider.CoverGuardOverSwing(taunt, all, snap, snap.Performer, "tank_riposte"));
            Assert.Same(taunt, TurnDecider.CoverGuardOverSwing(taunt, all, snap, snap.Performer, "any"));

            // A cited boss gate (Denial meltdown guard, crisis heal ...) is not a swing.
            Assert.Same(crush, TurnDecider.CoverGuardOverSwing(crush, all, snap, snap.Performer, "meltdown_guard"));
            crush.NoteReason = "fall_taunt";
            Assert.Same(crush, TurnDecider.CoverGuardOverSwing(crush, all, snap, snap.Performer, "attack"));

            // A plain pass is replaced (it covers nobody).
            var pass = Act("pass_stress", SkillKind.Pass, snap.Performer, -50f);
            all.Add(pass);
            Assert.Same(guardHwm, TurnDecider.CoverGuardOverSwing(pass, all, snap, snap.Performer, "any"));
        }

        // Logged turn: Hits [MAA (Riposte return) 6.0, guardian 1.3].
        // The guardian bar is the enemy, never the performer.
        [Fact]
        public void GuardBarSkipsThePerformersRiposteHit()
        {
            var snap = new TurnSnapshot();
            var maa = Hero(Maa, "man_at_arms", 1, 0f, 46f);
            maa.DeathsDoor = true;
            snap.Heroes.Add(maa);
            var mongrel = Enemy(Foe, "shared_pillager_mongrel", 0, 12f);
            mongrel.Riposte = true;
            var ranged = Enemy(Foe2, "shared_pillager_ranged", 2, 18f);
            snap.Enemies.Add(mongrel);
            snap.Enemies.Add(ranged);
            snap.PerformerGuid = Maa;
            snap.Performer = maa;

            var preview = new PreviewScore { Ok = true, Damage = 7.3f, GuardGuid = Foe2 };
            preview.Hits.Add(new PreviewHit { Guid = Maa, Damage = 6f });
            preview.Hits.Add(new PreviewHit { Guid = Foe, Damage = 1.3f });
            preview.HitGuids.AddRange(new[] { Maa, Foe, Foe2 });

            Assert.Equal(Foe, TurnDecider.GuardBarGuid(preview, ranged, TurnDecider.HeroGuidTest(snap)));
            TurnDecider.NoteKillFromHp(preview, ranged, snap);
            Assert.False(preview.Kills); // 1.3 into a 12 HP guardian, not the MAA's 0 HP bar

            Assert.Equal(-TurnDecider.SelfHitOnDeathsDoorPenalty, TurnDecider.SelfHitPenalty(preview, maa));
            maa.DeathsDoor = false; maa.Hp = 5f; maa.HpPct = 5f / 46f;
            Assert.Equal(-TurnDecider.SelfHitIntoDeathsDoorPenalty, TurnDecider.SelfHitPenalty(preview, maa));
            maa.Hp = 30f; maa.HpPct = 30f / 46f;
            Assert.Equal(0f, TurnDecider.SelfHitPenalty(preview, maa));
        }

        [Fact]
        public void PassIsNotSwappedForARiposteOrAGuardOnDeathsDoor()
        {
            var snap = Snap(0f, 20f);
            snap.Performer.DeathsDoor = true;
            var into = Act("maa_crush", SkillKind.Attack, snap.Find(Foe2), -184f, dmg: 2.6f);
            into.Preview.Hits.Add(new PreviewHit { Guid = Maa, Damage = 6f });
            var guard = Act("maa_defender", SkillKind.Support, snap.Find(Gr), -73f);
            var pass = Act("pass_stress", SkillKind.Pass, snap.Performer, -50f);
            var all = new List<ScoredAction> { pass, into, guard };
            var pick = TurnDecider.FreeOverPass(pass, all, new EnemyFocus(), snap, snap.Performer, 2);
            Assert.Same(pass, pick);
        }
    }
}
