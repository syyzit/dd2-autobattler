using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace Dd2Autobattler.Search
{
    internal enum SearchMode
    {
        Off,
        // Greedy plays; the search pick and value are logged next to it.
        Compare,
        // The search pick plays when it beats greedy by MinGain.
        On
    }

    /// <summary>
    /// Tunables for the round lookahead. Plain fields so the replay tool and
    /// tests set them directly; Bind wires them to the BepInEx config.
    /// </summary>
    internal sealed class SearchSettings
    {
        public SearchMode Mode = SearchMode.Off;
        // Root alternatives must score within Margin of the greedy pick.
        public float Margin = 45f;
        // At most this many root alternatives (best greedy score first).
        public int MaxRoot = 6;
        // Search overrides greedy only when it gains at least this much.
        public float MinGain = 4f;
        // Weight of the modelled rest-of-round value against the greedy score.
        public float FutureWeight = 1f;
        // Allies' top-K model actions branched at the first ally turn (1 = policy only).
        public int AllyBranch = 2;
        // Later heroes are modelled from CSV damage and a one-step policy, not
        // their real scorer: their modelled actions land with this weight.
        public float AllyTrust = 0.75f;
        // Simulated actor turns per decision (deterministic budget).
        public int NodeBudget = 600;
        // Wall-clock safety cap; exceeding it abandons the search (greedy plays).
        public float HardCapMs = 8f;
        // Board evaluation weights (HP units).
        public float DoorPenalty = 40f;
        public float DeathPenalty = 250f;
        public float KillBonus = 15f;
        public float ThreatRounds = 2f;
        public float LowHpWeight = 1.5f;

        public SearchSettings Clone()
        {
            return (SearchSettings)MemberwiseClone();
        }
    }

    internal static class SearchConfig
    {
        // Live settings (default OFF). The replay tool passes its own.
        public static SearchSettings Current = new SearchSettings();

        private static ConfigEntry<SearchMode> _mode;
        private static ConfigEntry<float> _margin;
        private static ConfigEntry<float> _minGain;
        private static ConfigEntry<int> _budget;
        private static ConfigEntry<float> _capMs;

        private static string _excelDir;
        private static GameData _data;

        public static void Bind(ConfigFile config)
        {
            _mode = config.Bind("Search", "Mode", SearchMode.Off,
                "Round lookahead on top of the greedy scorer. Off; Compare = greedy plays and the search pick/value are logged (field \"search\"); On = the search pick plays when it beats greedy by MinGain. See notes/search.md.");
            _margin = config.Bind("Search", "Margin", 45f,
                "Search only considers picks the greedy scorer rates within this many points of its own pick.");
            _minGain = config.Bind("Search", "MinGain", 4f,
                "Search overrides greedy only when its value is at least this much higher.");
            _budget = config.Bind("Search", "NodeBudget", 600,
                "Simulated actor turns per decision (deterministic).");
            _capMs = config.Bind("Search", "HardCapMs", 8f,
                "Wall-clock cap per decision in ms; past it the search is dropped and greedy plays.");
            _mode.SettingChanged += (s, e) => Sync();
            _margin.SettingChanged += (s, e) => Sync();
            _minGain.SettingChanged += (s, e) => Sync();
            _budget.SettingChanged += (s, e) => Sync();
            _capMs.SettingChanged += (s, e) => Sync();
            try
            {
                _excelDir = Path.Combine(Path.Combine(Path.Combine(Paths.GameRootPath, "Darkest Dungeon II_Data"), "StreamingAssets"), "Excel");
            }
            catch
            {
                _excelDir = null;
            }
            Sync();
        }

        private static void Sync()
        {
            var s = Current.Clone();
            if (_mode != null) s.Mode = _mode.Value;
            if (_margin != null) s.Margin = _margin.Value;
            if (_minGain != null) s.MinGain = _minGain.Value;
            if (_budget != null) s.NodeBudget = Math.Max(10, _budget.Value);
            if (_capMs != null) s.HardCapMs = Math.Max(0.5f, _capMs.Value);
            Current = s;
            // Load the CSVs up front (plugin start / when turned on), not
            // inside the first combat frame.
            if (s.Mode != SearchMode.Off)
                Data();
        }

        // Game CSV data for the live game (null when not found).
        public static GameData Data()
        {
            if (_data != null)
                return _data;
            if (string.IsNullOrEmpty(_excelDir))
                return null;
            try
            {
                _data = GameData.Load(_excelDir);
            }
            catch (Exception)
            {
                _data = new GameData();
            }
            return _data;
        }
    }
}
