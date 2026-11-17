using Dd2Autobattler.Combat;
using Xunit;
using ScoredAction = Dd2Autobattler.Combat.TurnDecider.ScoredAction;

namespace Dd2Autobattler.Tests
{
    // Sure must-kill finish over a non-urgent crisis heal, and the Shambler note
    // (tentacles must-kill, Shambler deferred-open, finished when low).
    public class MustKillShamblerTests
    {
        private static TargetInfo Body(uint guid, string cls, int team, float hp, float max, bool door = false)
        {
            return new TargetInfo { Guid = guid, ClassId = cls, Team = team, Living = true, Hp = hp, HpMax = max, HpPct = hp / max, DeathsDoor = door };
        }

        private static ScoredAction Hit(TargetInfo t, bool kills, float hit = 1f)
        {
            return new ScoredAction
            {
                SkillId = "gr_poison_dart",
                TargetGuid = t.Guid,
                Kind = SkillKind.Attack,
                EnemyTarget = true,
                Target = t,
                Preview = new PreviewScore { Ok = true, Damage = 2f, Kills = kills, HitChance = hit },
                Score = 150f
            };
        }

        private static ScoredAction Heal(TargetInfo t)
        {
            return new ScoredAction
            {
                SkillId = "gr_artemisia",
                TargetGuid = t.Guid,
                Kind = SkillKind.Heal,
                Target = t,
                Preview = new PreviewScore { Ok = true, Heal = 12f },
                Score = 165f
            };
        }

        private static EnemyFocus BishopFocus(TargetInfo bishop)
        {
            var f = new EnemyFocus { HasMustKillFirst = true, HasPriorityTarget = true };
            f.Enemies.Add(new EnemyThreat { Guid = bishop.Guid, ClassId = bishop.ClassId, MustKillFirst = true });
            return f;
        }

        [Fact]
        public void SureMustKillFinishBeatsNonUrgentHeal()
        {
            var bishop = Body(20, "lost_battalion_bishop", 1, 1f, 22f);
            var gr = Body(3, "grave_robber", 0, 10f, 35f);
            Assert.True(TurnDecider.MustKillFinishBeatsHeal(Hit(bishop, true), Heal(gr), BishopFocus(bishop)));
        }

        [Fact]
        public void HealOnDeathsDoorStillBeatsTheFinish()
        {
            var bishop = Body(20, "lost_battalion_bishop", 1, 1f, 22f);
            var gr = Body(3, "grave_robber", 0, 0f, 35f, door: true);
            Assert.False(TurnDecider.MustKillFinishBeatsHeal(Hit(bishop, true), Heal(gr), BishopFocus(bishop)));
        }

        [Fact]
        public void NoFinishOverHealWhenNotSureOrNotMustKill()
        {
            var bishop = Body(20, "lost_battalion_bishop", 1, 1f, 22f);
            var gr = Body(3, "grave_robber", 0, 10f, 35f);
            Assert.False(TurnDecider.MustKillFinishBeatsHeal(Hit(bishop, true, 0.6f), Heal(gr), BishopFocus(bishop)));
            Assert.False(TurnDecider.MustKillFinishBeatsHeal(Hit(bishop, false), Heal(gr), BishopFocus(bishop)));
            var foot = Body(21, "lost_battalion_foot_soldier", 1, 1f, 17f);
            Assert.False(TurnDecider.MustKillFinishBeatsHeal(Hit(foot, true), Heal(gr), BishopFocus(bishop)));
        }

        // Shambler 30, tentacles 31 (fresh, 12 HP) and 32 (block x blocks32, hp32).
        // The performer's legal clicks: one on each enemy with the given kill flags.
        private static TurnSnapshot ShamblerSnap(int blocks32, float hp32, bool kills32, float hit32 = 1f, bool tentacles = true)
        {
            var snap = new TurnSnapshot();
            snap.Enemies.Add(new TargetInfo { Guid = 30, ClassId = "shared_shambler", Team = 1, Living = true, Hp = 70f, HpMax = 85f, HpPct = 70f / 85f, Size = 2 });
            snap.Legal.Add(new LegalSnap { Skill = "gr_pick_to_the_face_u", Target = 30, Preview = new PreviewScore { Ok = true, Damage = 7f } });
            if (tentacles)
            {
                snap.Enemies.Add(new TargetInfo { Guid = 31, ClassId = "shared_shambler_tentacle", Team = 1, Living = true, Hp = 12f, HpMax = 12f, HpPct = 1f });
                var grown = new TargetInfo { Guid = 32, ClassId = "shared_shambler_tentacle", Team = 1, Living = true, Hp = hp32, HpMax = 12f, HpPct = hp32 / 12f, BlockCount = blocks32 };
                grown.Tokens = new System.Collections.Generic.List<string>();
                for (var i = 0; i < blocks32; i++)
                    grown.Tokens.Add("block");
                snap.Enemies.Add(grown);
                snap.Legal.Add(new LegalSnap { Skill = "gr_pick_to_the_face_u", Target = 31, Preview = new PreviewScore { Ok = true, Damage = 7f } });
                snap.Legal.Add(new LegalSnap { Skill = "gr_pick_to_the_face_u", Target = 32, Preview = new PreviewScore { Ok = true, Damage = 3f, Kills = kills32, HitChance = hit32 } });
            }
            snap.Reindex();
            return snap;
        }

        private static EnemyFocus ShamblerFocus(TurnSnapshot snap)
        {
            var f = new EnemyFocus();
            foreach (var e in snap.Enemies)
            {
                var shambler = e.ClassId == "shared_shambler";
                f.Enemies.Add(new EnemyThreat { Guid = e.Guid, ClassId = e.ClassId, Boss = shambler, Summons = shambler, Add = !shambler, Size = e.Size, HpPct = e.HpPct });
            }
            EnemyFocus.ApplyShamblerNote(f, snap);
            EnemyFocus.MarkNonPriorityAdds(f);
            EnemyFocus.ScoreEnemies(f);
            return f;
        }

        [Fact]
        public void ShamblerIsTheMustKillAndTentaclesAreOpen()
        {
            // Grown but not killable this click: still the Shambler.
            var f = ShamblerFocus(ShamblerSnap(blocks32: 2, hp32: 10f, kills32: false));
            Assert.True(f.IsMustKillFirst(30));
            Assert.False(f.IsDeferred(30));
            Assert.False(f.IsMustKillFirst(31));
            Assert.True(f.IsOpenWhenUnreached(31));
            Assert.True(f.IsOpenWhenUnreached(32));
            Assert.True(f.CultistOpenOverIdle);
            Assert.True(f.ScoreOf(30) > f.ScoreOf(31));
            // Among open tentacles the grown one (Block) ranks first.
            Assert.True(f.ScoreOf(32) > f.ScoreOf(31));
            var alone = ShamblerFocus(ShamblerSnap(0, 12f, false, tentacles: false));
            Assert.True(alone.IsMustKillFirst(30));
        }

        [Fact]
        public void SureKillOnAGrownTentacleIsThisTurnsMustKill()
        {
            var f = ShamblerFocus(ShamblerSnap(blocks32: 2, hp32: 2f, kills32: true));
            Assert.True(f.IsMustKillFirst(32));
            Assert.False(f.IsMustKillFirst(30));
            Assert.True(f.IsOpenWhenUnreached(30));
            Assert.False(f.IsMustKillFirst(31));
        }

        [Fact]
        public void FreshOrUnsureTentacleKillDoesNotBeatTheShambler()
        {
            // No Block: it never landed a Slap, a fresh one would replace it anyway.
            Assert.True(ShamblerFocus(ShamblerSnap(blocks32: 0, hp32: 2f, kills32: true)).IsMustKillFirst(30));
            // Grown, but the kill is not sure.
            Assert.True(ShamblerFocus(ShamblerSnap(blocks32: 2, hp32: 2f, kills32: true, hit32: 0.7f)).IsMustKillFirst(30));
        }
    }
}
