using Dd2Autobattler.Logging;
using Xunit;

namespace Dd2Autobattler.Tests
{
    // action_result hp_after (combat outcome logging).
    public class OutcomeLogTests
    {
        [Theory]
        [InlineData(28f, 40f, 8f, 0f, 20f)]
        [InlineData(5f, 40f, 9f, 0f, 0f)]
        [InlineData(30f, 40f, 0f, 15f, 40f)]
        public void HpAfterAppliesTheHit(float before, float max, float dmg, float heal, float expected)
        {
            Assert.Equal(expected, OutcomeLog.HpAfter(before, max, dmg, heal));
        }
    }
}