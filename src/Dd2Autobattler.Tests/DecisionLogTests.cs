using System;
using Dd2Autobattler.Logging;
using Xunit;

namespace Dd2Autobattler.Tests
{
    // decisions.jsonl gets attached to bug reports: no record may carry a local folder.
    public class DecisionLogTests
    {
        [Fact]
        public void SessionStartPathIsRelativeToBepInEx()
        {
            var rec = DecisionLog.SessionStartRecord("20250101-120000", "1.0", new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));
            Assert.Equal("session_start", rec.Value<string>("type"));
            Assert.Equal("20250101-120000", rec.Value<string>("session"));
            Assert.Equal("Dd2Autobattler/logs/20250101-120000/decisions.jsonl", rec.Value<string>("path"));
            var text = rec.ToString();
            Assert.DoesNotContain(":\\", text);
            Assert.DoesNotContain(":/", text);
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile))
                Assert.DoesNotContain(profile, text, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData(@"Could not find file 'C:\Games\DD2\BepInEx\config\x.cfg'", @"Could not find file 'BepInEx\config\x.cfg'")]
        [InlineData(@"Access to the path 'c:/games/dd2/bepinex/plugins' is denied", @"Access to the path 'BepInEx/plugins' is denied")]
        [InlineData(@"Missing C:\Users\someone\AppData\x.txt", @"Missing %USERPROFILE%\AppData\x.txt")]
        [InlineData("plain message", "plain message")]
        public void ScrubTakesLocalFoldersOut(string input, string expected)
        {
            Assert.Equal(expected, DecisionLog.Scrub(input, @"C:\Games\DD2\BepInEx\", @"C:\Users\someone"));
        }

        [Fact]
        public void ScrubLeavesTextAloneWithoutFolders()
        {
            Assert.Equal("x", DecisionLog.Scrub("x", null, ""));
            Assert.Null(DecisionLog.Scrub(null, @"C:\a\b", @"C:\Users\b"));
        }
    }
}
