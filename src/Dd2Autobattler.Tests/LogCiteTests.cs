using Newtonsoft.Json.Linq;
using Xunit;

namespace Dd2Autobattler.Tests
{
    public sealed class LogCiteTests
    {
        [Fact]
        public void Obsession_flashing_on_full_clusters_is_stalk_aoe_chip()
        {
            var turn = Turn(
                "gr_flashing_daggers_u",
                "focus_eyes",
                Legal("gr_flashing_daggers_u", 159.7f, kills: false, hitN: 2, why: "boss+eyes"),
                Legal("gr_thrown_dagger_u", 152.2f, kills: false, hitN: 1, why: "boss+eyes"));
            turn["enemies"] = new JArray { Enemy("boss_eyes_stalk_l") };
            var hits = LogCite.Check(turn);
            Assert.Contains("stalk_aoe_chip", hits);
            Assert.DoesNotContain("setup_over_kill", hits);
        }

        [Fact]
        public void Obsession_laudanum_over_a_stalk_kill_is_setup_over_kill()
        {
            var turn = Turn(
                "laudanum",
                "item_stress",
                Legal("gr_pick_to_the_face", 144.8f, kills: true, hitN: 1, why: "boss+eyes"),
                Legal("laudanum", 55f, kills: false, hitN: 0, why: "", kind: "Support"));
            turn["enemies"] = new JArray { Enemy("boss_eyes_stalk_l") };
            var hits = LogCite.Check(turn);
            Assert.Contains("setup_over_kill", hits);
        }

        [Fact]
        public void Obsession_peel_over_near_kill_is_stalk_skip_kill()
        {
            var turn = Turn(
                "gr_thrown_dagger_u",
                "peel",
                Legal("gr_thrown_dagger_u", 146.8f, kills: false, hitN: 1, why: "boss+eyes"),
                Legal("gr_pick_to_the_face", 144.7f, kills: true, hitN: 1, why: "boss+eyes"));
            turn["enemies"] = new JArray { Enemy("boss_eyes_stalk_l") };
            var hits = LogCite.Check(turn);
            Assert.Contains("stalk_skip_kill", hits);
        }

        [Fact]
        public void DoT_aoe_on_stalks_is_allowed()
        {
            var blight = Legal("pd_noxious_blast", 101f, kills: false, hitN: 2, why: "boss+eyes");
            blight["apply_blight"] = 3.5f;
            blight["skill"] = "pd_noxious_blast";
            var turn = Turn("pd_noxious_blast", "focus_eyes", blight);
            turn["enemies"] = new JArray { Enemy("boss_eyes_stalk_l") };
            var hits = LogCite.Check(turn);
            Assert.DoesNotContain("stalk_aoe_chip", hits);
        }

        [Fact]
        public void Obsession_laudanum_over_a_high_swing_is_setup_over_swing()
        {
            var turn = Turn(
                "laudanum",
                "item_stress",
                Legal("gr_flashing_daggers_u", 156.4f, kills: false, hitN: 2, why: "boss+eyes"),
                Legal("laudanum", 24f, kills: false, hitN: 0, why: "", kind: "Support"));
            turn["chosen"]["score"] = 24f;
            turn["enemies"] = new JArray { Enemy("boss_eyes_stalk_l") };
            var hits = LogCite.Check(turn);
            Assert.Contains("setup_over_swing", hits);
            Assert.DoesNotContain("setup_over_kill", hits);
        }

        [Fact]
        public void Free_laudanum_is_not_a_setup_cite()
        {
            var laud = Legal("laudanum", 55f, kills: false, hitN: 0, why: "", kind: "Support");
            laud["item_free"] = true;
            laud["item"] = true;
            var turn = Turn(
                "laudanum",
                "item_stress",
                Legal("gr_pick_to_the_face", 144.8f, kills: true, hitN: 1, why: "boss+eyes"),
                laud);
            turn["enemies"] = new JArray { Enemy("boss_eyes_stalk_l") };
            var hits = LogCite.Check(turn);
            Assert.DoesNotContain("setup_over_kill", hits);
            Assert.DoesNotContain("setup_over_swing", hits);
        }

        [Fact]
        public void Trash_fight_does_not_fire_stalk_cites()
        {
            var turn = Turn(
                "gr_flashing_daggers_u",
                "preview_damage",
                Legal("gr_flashing_daggers_u", 80f, kills: false, hitN: 2, why: "trash"));
            turn["enemies"] = new JArray { Enemy("lost_battalion_foot_soldier") };
            Assert.Empty(LogCite.Check(turn));
        }

        private static JObject Turn(string skill, string reason, params JObject[] legal)
        {
            return new JObject
            {
                ["reason"] = reason,
                ["chosen"] = new JObject { ["skill"] = skill, ["target"] = 1, ["reason"] = reason, ["score"] = legal[0].Value<float>("score") },
                ["legal"] = new JArray(legal)
            };
        }

        private static JObject Legal(string skill, float score, bool kills, int hitN, string why, string kind = "Attack")
        {
            return new JObject
            {
                ["skill"] = skill,
                ["target"] = skill.StartsWith("laudanum") ? 9 : 1,
                ["score"] = score,
                ["kills"] = kills,
                ["hit_n"] = hitN,
                ["kind"] = kind,
                ["enemy"] = kind == "Attack",
                ["focus_why"] = why,
                ["apply_bleed"] = 0f,
                ["apply_blight"] = 0f,
                ["apply_burn"] = 0f
            };
        }

        private static JObject Enemy(string classId)
        {
            return new JObject { ["class"] = classId, ["guid"] = 1 };
        }
    }
}
