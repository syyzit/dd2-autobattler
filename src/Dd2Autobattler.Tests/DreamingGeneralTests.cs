using System.Collections.Generic;
using Dd2Autobattler.Combat;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public class DreamingGeneralTests
    {
        [Fact]
        public void Taproot_budget_never_arms_waking_dead()
        {
            // 2 taproot tokens: the next tap is the 3rd (dreaming_general_taproot_ready).
            Assert.Equal(0, EnemyFocus.DreamingTaprootBudget(4, 2, 2, 0));
            Assert.Equal(0, EnemyFocus.DreamingTaprootBudget(4, 3, 1, 0));
            // One safe tap left at 1 token, even with two locks.
            Assert.Equal(1, EnemyFocus.DreamingTaprootBudget(4, 1, 2, 0));
            Assert.Equal(2, EnemyFocus.DreamingTaprootBudget(4, 0, 2, 0));
        }

        [Fact]
        public void Taproot_budget_only_when_the_retract_frees_a_lock()
        {
            // Round 1 / nobody locked / a tangle_b hero could take the random retract.
            Assert.Equal(0, EnemyFocus.DreamingTaprootBudget(1, 0, 1, 0));
            Assert.Equal(0, EnemyFocus.DreamingTaprootBudget(3, 0, 0, 3));
            Assert.Equal(0, EnemyFocus.DreamingTaprootBudget(3, 0, 1, 1));
            Assert.Equal(1, EnemyFocus.DreamingTaprootBudget(3, 0, 1, 0));
        }

        [Fact]
        public void Taproot_tokens_count_exact_ids()
        {
            var root = new TargetInfo { Tokens = new List<string> { "taproot", "taproot", "taproot_target" } };
            Assert.Equal(2, EnemyFocus.TaprootTokens(root));
            Assert.Equal(0, EnemyFocus.TaprootTokens(new TargetInfo()));
        }
    }
}
