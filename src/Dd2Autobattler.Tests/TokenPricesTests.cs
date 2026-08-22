using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public sealed class TokenPricesTests
    {
        public TokenPricesTests()
        {
            CombatMemory.ResetFight();
        }

        [Fact]
        public void Hitting_combo_without_spending_costs_32()
        {
            var preview = new PreviewScore { Ok = true };
            var target = new TargetInfo { Combo = true, Hp = 20f };
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, null, 1, null);
            Assert.Equal(-32f, eval.Score);
            Assert.Equal("save_combo", eval.Reason);
        }

        [Fact]
        public void Applying_combo_with_a_follow_up_spender_is_14()
        {
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 20f };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true });
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, party, 1, null);
            Assert.Equal(14f, eval.Score);
            Assert.Equal("apply_combo", eval.Reason);
        }

        [Fact]
        public void Applying_combo_after_the_spender_already_acted_is_zero()
        {
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 20f };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true });
            CombatMemory.NoteComboSpenderActed(2);
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, party, 1, null);
            Assert.Equal(0f, eval.Score);
        }
    }
}
