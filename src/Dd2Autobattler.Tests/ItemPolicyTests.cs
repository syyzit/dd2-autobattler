using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public sealed class ItemPolicyTests
    {
        [Fact]
        public void Deaths_door_item_heal_is_90_and_use_now()
        {
            var preview = new PreviewScore { Ok = true, Heal = 8f, HealValid = true };
            var target = new TargetInfo { DeathsDoor = true, Hp = 1f, HpPct = 0.01f };
            var eval = ItemPolicy.Evaluate("food", null, SkillKind.Heal, false, preview, target, null, 3, 3);
            Assert.Equal(90f, eval.Score);
            Assert.True(eval.UseNow);
            Assert.True(eval.Crisis);
            Assert.Equal("item_heal_dd", eval.Reason);
        }

        [Fact]
        public void Corpse_attack_item_is_minus_250()
        {
            var preview = new PreviewScore { Ok = true, Damage = 12f };
            var target = new TargetInfo { Corpse = true, Hp = 0f };
            var eval = ItemPolicy.Evaluate("grenade", null, SkillKind.Attack, true, preview, target, null, 3, 3);
            Assert.Equal(-250f, eval.Score);
            Assert.Equal("item_skip_corpse", eval.Reason);
        }

        [Fact]
        public void Stress_9_item_is_55_crisis()
        {
            var preview = new PreviewScore { Ok = true };
            var target = new TargetInfo { Stress = 9f, Hp = 20f, HpPct = 1f };
            var eval = ItemPolicy.Evaluate("laudanum", null, SkillKind.Support, false, preview, target, null, 3, 3);
            Assert.Equal(55f, eval.Score);
            Assert.True(eval.Crisis);
            Assert.True(eval.UseNow);
            Assert.Equal("item_stress", eval.Reason);
        }

        [Fact]
        public void Use_threshold_is_18()
        {
            Assert.Equal(18f, ItemPolicy.UseThreshold);
        }

        [Fact]
        public void Pouch_of_lye_on_corpse_with_one_enemy_is_use_now()
        {
            var preview = new PreviewScore { Ok = true };
            var target = new TargetInfo { Corpse = true, Hp = 11f };
            var eval = ItemPolicy.Evaluate("pouch_of_lye", null, SkillKind.Attack, true, preview, target, null, 1, 2);
            Assert.True(eval.UseNow);
            Assert.True(eval.Crisis);
            Assert.Equal("item_clear_corpse", eval.Reason);
            Assert.True(eval.Score >= ItemPolicy.UseThreshold);
        }

        [Fact]
        public void Grenade_on_corpse_is_still_skipped()
        {
            var preview = new PreviewScore { Ok = true, Damage = 12f };
            var target = new TargetInfo { Corpse = true, Hp = 0f };
            var eval = ItemPolicy.Evaluate("fire_grenade", null, SkillKind.Attack, true, preview, target, null, 1, 3);
            Assert.Equal(-250f, eval.Score);
            Assert.Equal("item_skip_corpse", eval.Reason);
        }

        [Fact]
        public void Rag_on_blind_is_strip_use_now()
        {
            var preview = new PreviewScore { Ok = true };
            var target = new TargetInfo { Blind = true, Hp = 20f, HpPct = 1f };
            var eval = ItemPolicy.Evaluate("rag", null, SkillKind.Support, false, preview, target, null, 3, 2);
            Assert.True(eval.UseNow);
            Assert.True(eval.Crisis);
            Assert.Equal("item_strip", eval.Reason);
        }

        [Fact]
        public void Single_leech_on_ally_is_disease_use_now()
        {
            var preview = new PreviewScore { Ok = true };
            var target = new TargetInfo { Hp = 20f, HpPct = 1f };
            var eval = ItemPolicy.Evaluate("single_leech", null, SkillKind.Support, false, preview, target, null, 3, 2);
            Assert.True(eval.UseNow);
            Assert.Equal("item_disease", eval.Reason);
        }
    }
}
