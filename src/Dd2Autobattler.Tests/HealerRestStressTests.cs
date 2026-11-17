using System.Collections.Generic;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Logging;
using Dd2Autobattler.Replay;
using Newtonsoft.Json.Linq;
using Xunit;
using ScoredAction = Dd2Autobattler.Combat.TurnDecider.ScoredAction;

namespace Dd2Autobattler.Tests
{
    // Healer and stress fixes (from logged fights):
    // Rest instead of Battlefield Medicine on the PD herself (rest_keeps_medicine),
    // Rest at 8+ stress off Death's Door is a Meltdown, not a heal, and Rest does
    // not block the walk back onto a heal launch rank. Plus the two tool fixes:
    // fight_end party state for dead heroes and replay fight numbering.
    public class HealerRestStressTests
    {
        private const uint Pd = 4, Maa = 1, Foe = 10;

        private static TargetInfo Hero(uint guid, string cls, int rank, float hp, float max, float stress = 0f)
        {
            return new TargetInfo
            {
                Guid = guid, ClassId = cls, Team = 0, Rank = rank, Living = true, Hp = hp, HpMax = max,
                HpPct = hp / max, Stress = stress, DeathsDoor = hp <= 0f, Tokens = new List<string>()
            };
        }

        private static ScoredAction Act(string skill, SkillKind kind, TargetInfo target, float score, float heal = 0f)
        {
            return new ScoredAction
            {
                SkillId = skill,
                TargetGuid = target.Guid,
                Kind = kind,
                EnemyTarget = target.Team == 1,
                Target = target,
                Preview = new PreviewScore { Ok = true, Heal = heal },
                Score = score
            };
        }

        private static List<ScoredAction> PdKit(TargetInfo pd, TargetInfo ally, out ScoredAction medSelf, out ScoredAction medAlly, out ScoredAction rest)
        {
            medSelf = Act("pd_battlefield_medicine_u", SkillKind.Heal, pd, 293f, heal: 7f);
            medAlly = Act("pd_battlefield_medicine_u", SkillKind.Heal, ally, 233f, heal: 12f);
            rest = Act("pass_heal", SkillKind.Heal, pd, 291f, heal: 5.4f);
            var foe = new TargetInfo { Guid = Foe, Team = 1, Living = true, Hp = 20f, HpMax = 20f, HpPct = 1f, Tokens = new List<string>() };
            var gas = Act("pd_blinding_gas", SkillKind.Attack, foe, -30f);
            return new List<ScoredAction> { medSelf, rest, medAlly, gas };
        }

        // Logged r3: PD on Death's Door at 1 stress, Medicine on
        // herself 293 vs Rest 291; the charges then ran out with allies dying.
        [Fact]
        public void RestBeatsMedicineOnSelf()
        {
            var pd = Hero(Pd, "plague_doctor", 2, 0f, 27f, stress: 1f);
            var ally = Hero(5, "vestal", 3, 0f, 49f);
            ScoredAction medSelf, medAlly, rest;
            var all = PdKit(pd, ally, out medSelf, out medAlly, out rest);
            TurnDecider.ApplyRestOverSelfMedicine(all, pd);
            Assert.True(medSelf.Score < rest.Score);
            Assert.Equal(233f, medAlly.Score);
            Assert.Equal("rest_keeps_medicine", rest.NoteReason);
        }

        [Fact]
        public void MedicineStaysWhenRestCostsTooMuch()
        {
            var ally = Hero(5, "vestal", 3, 0f, 49f);
            ScoredAction medSelf, medAlly, rest;

            // 6 stress: Rest would leave her at 8, one Doubt from Meltdown.
            var stressed = Hero(Pd, "plague_doctor", 2, 0f, 27f, stress: 6f);
            var all = PdKit(stressed, ally, out medSelf, out medAlly, out rest);
            TurnDecider.ApplyRestOverSelfMedicine(all, stressed);
            Assert.Equal(293f, medSelf.Score);
            Assert.Null(rest.NoteReason);

            // Her DoT tick (8) would put her back on 0 HP after a 5.4 Rest: Medicine clears it.
            var dotted = Hero(Pd, "plague_doctor", 2, 0f, 27f, stress: 1f);
            dotted.NextDot = 8f;
            all = PdKit(dotted, ally, out medSelf, out medAlly, out rest);
            TurnDecider.ApplyRestOverSelfMedicine(all, dotted);
            Assert.Equal(293f, medSelf.Score);

            // A small DoT the Rest outheals does not stop the swap.
            var small = Hero(Pd, "plague_doctor", 2, 2f, 27f, stress: 1f);
            small.NextDot = 3f;
            all = PdKit(small, ally, out medSelf, out medAlly, out rest);
            TurnDecider.ApplyRestOverSelfMedicine(all, small);
            Assert.True(medSelf.Score < rest.Score);
        }

        [Fact]
        public void RestIntoMeltdownOnlyOffDeathsDoor()
        {
            // CSV pass_heal stress_damage_2; overstress meltdown_health = HP down to 10%.
            Assert.True(TurnDecider.RestIntoMeltdown("pass_heal", Hero(Maa, "man_at_arms", 0, 9f, 55f, stress: 9f)));
            Assert.True(TurnDecider.RestIntoMeltdown("pass_heal", Hero(Maa, "man_at_arms", 0, 11f, 55f, stress: 8f)));
            Assert.False(TurnDecider.RestIntoMeltdown("pass_heal", Hero(Maa, "man_at_arms", 0, 11f, 55f, stress: 7f)));
            // On Death's Door the Rest still lifts him off it.
            Assert.False(TurnDecider.RestIntoMeltdown("pass_heal", Hero(Maa, "man_at_arms", 0, 0f, 55f, stress: 9f)));
            // Already under 10%: the Meltdown cannot take him lower.
            Assert.False(TurnDecider.RestIntoMeltdown("pass_heal", Hero(Maa, "man_at_arms", 0, 4f, 55f, stress: 9f)));
            Assert.False(TurnDecider.RestIntoMeltdown("pass_stress", Hero(Maa, "man_at_arms", 0, 11f, 55f, stress: 9f)));
        }

        [Fact]
        public void OverstressingRestIsNotACrisisHealNorAMedicineSwap()
        {
            var maa = Hero(Maa, "man_at_arms", 0, 9f, 55f, stress: 9f);
            var p = new PreviewScore { Ok = true, Heal = 11f };
            Assert.False(TurnDecider.IsCrisisHealClick(SkillKind.Heal, "pass_heal", false, maa, p));
            maa.Stress = 5f;
            Assert.True(TurnDecider.IsCrisisHealClick(SkillKind.Heal, "pass_heal", false, maa, p));

            // The PD at 5 stress is under the swap cap, but at 8+ the Rest is out anyway.
            var ally = Hero(5, "vestal", 3, 0f, 49f);
            var pd = Hero(Pd, "plague_doctor", 2, 5f, 27f, stress: 8f);
            ScoredAction medSelf, medAlly, rest;
            var all = PdKit(pd, ally, out medSelf, out medAlly, out rest);
            TurnDecider.ApplyRestOverSelfMedicine(all, pd);
            Assert.Equal(293f, medSelf.Score);
        }

        [Fact]
        public void DeadHeroIsReportedDeadWithZeroHp()
        {
            // Logged fight_end forest_dungeon_3: grave_robber_corpse hp 16.
            var o = new JObject { ["guid"] = 3929, ["class"] = "grave_robber_corpse", ["hp"] = 16f, ["hp_max"] = 16f, ["living"] = false, ["corpse"] = true, ["deaths_door"] = false };
            OutcomeLog.MarkDead(o);
            Assert.Equal(0f, o.Value<float>("hp"));
            Assert.True(o.Value<bool>("dead"));
            Assert.Equal(16f, o.Value<float>("corpse_hp"));
            Assert.Equal("grave_robber", o.Value<string>("class"));

            var alive = new JObject { ["class"] = "plague_doctor", ["hp"] = 6f, ["living"] = true };
            OutcomeLog.MarkDead(alive);
            Assert.Equal(6f, alive.Value<float>("hp"));
            Assert.Null(alive["dead"]);
        }

        [Fact]
        public void TurnsAfterFightEndWithoutFightStartAreANewFight()
        {
            // In one log pillager_mash_202 ended, then lost_battalion_mash_204
            // (StoryResist) logged turns with no fight_start; both were #35.
            var c = new ReplayEngine.FightCounter();
            Assert.True(c.Next("fight_start", "pillager_mash_202", false));
            Assert.False(c.Next("turn", "pillager_mash_202", true));
            Assert.False(c.Next("fight_end", "pillager_mash_202", false));
            Assert.False(c.Next("node", "pillager_mash_202", false));
            Assert.True(c.Next("turn", "lost_battalion_mash_204", true));
            Assert.Equal(2, c.FightNo);
            Assert.False(c.Next("turn", "lost_battalion_mash_204", true));
            Assert.False(c.Next("fight_end", "lost_battalion_mash_204", false));
            Assert.True(c.Next("fight_start", "gaunt_mash_9", false));
            Assert.Equal(3, c.FightNo);
            Assert.Equal("gaunt_mash_9", c.FightId);
        }
    }
}
