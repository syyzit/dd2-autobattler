using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    // In-fight combat item scoring (ItemPolicy).
    public sealed class ItemSupplyScoringTests
    {
        [Fact]
        public void Laudanum_at_8_stress_is_crisis()
        {
            var preview = new PreviewScore { Ok = true };
            var target = new TargetInfo { Stress = 8f, Hp = 20f, HpPct = 1f };
            var eval = ItemPolicy.Evaluate("laudanum", null, SkillKind.Support, false, preview, target, null, 3, 3);
            Assert.Equal(40f, eval.Score);
            Assert.True(eval.Crisis);
            Assert.True(eval.UseNow);
        }

        [Fact]
        public void Cleanse_on_a_low_hero_with_a_small_dot_is_used()
        {
            var preview = new PreviewScore { Ok = true };
            var target = new TargetInfo { Hp = 10f, HpPct = 0.3f, BleedDot = 1f, NextDot = 1f };
            var eval = ItemPolicy.Evaluate("medicinal_herbs", null, SkillKind.Support, false, preview, target, null, 3, 1);
            Assert.Equal("item_cleanse_low", eval.Reason);
            Assert.True(eval.UseNow); // 32 - 6 (last one) = 26
        }

        [Fact]
        public void Healing_stress_item_on_a_35_pct_hero_scores_as_a_heal()
        {
            // CSV spring_water: tags heal, stress_heal; stress_heal_3, heal_amount_5.
            var def = new SkillSnap { Id = "spring_water", IsItemSkill = true, IsFree = true };
            def.Tags.Add("heal");
            def.Tags.Add("stress_heal");
            def.Tags.Add("combat_item");
            var preview = new PreviewScore { Ok = true, Heal = 5f, HealValid = true };
            var target = new TargetInfo { Hp = 12f, HpPct = 0.34f, Stress = 1f };
            var eval = ItemPolicy.Evaluate("spring_water", def, SkillKind.Support, false, preview, target, null, 3, 3);
            Assert.Equal("item_heal", eval.Reason);
            Assert.True(eval.UseNow);
            // Over 35% HP it stays a stress item (wasted at 1 stress).
            var healthy = new TargetInfo { Hp = 20f, HpPct = 0.5f, Stress = 1f };
            Assert.False(ItemPolicy.Evaluate("spring_water", def, SkillKind.Support, false, preview, healthy, null, 3, 3).UseNow);
        }
    }
}
