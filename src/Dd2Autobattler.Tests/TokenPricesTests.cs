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

        [Fact]
        public void Early_setup_is_rounds_1_and_2_with_a_pack()
        {
            Assert.False(TokenPrices.IsEarlySetup(0, 3));
            Assert.True(TokenPrices.IsEarlySetup(1, 3));
            Assert.True(TokenPrices.IsEarlySetup(2, 4));
            Assert.False(TokenPrices.IsEarlySetup(3, 4));
            Assert.False(TokenPrices.IsEarlySetup(1, 1));
            Assert.Equal(1.5f, TokenPrices.EarlySetupScale(1, 3));
            Assert.Equal(1f, TokenPrices.EarlySetupScale(3, 3));
        }

        [Fact]
        public void Early_round_pays_more_to_apply_combo()
        {
            CombatMemory.NoteRound(1);
            var preview = new PreviewScore { Ok = true };
            preview.ApplyTarget.Add("combo");
            var target = new TargetInfo { Hp = 20f };
            var party = new PartyKit();
            party.PartySpendsCombo = true;
            party.Heroes.Add(new HeroKit { Guid = 2, Living = true, SpendsCombo = true });
            var eval = TokenPrices.Evaluate(SkillKind.Attack, true, preview, target, 3, party, 1, null);
            Assert.Equal(21f, eval.Score);
        }
    }
}
