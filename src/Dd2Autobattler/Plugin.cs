using BepInEx;
using BepInEx.Configuration;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Logging;
using Dd2Autobattler.Ui;
using HarmonyLib;

namespace Dd2Autobattler
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "drednot.dd2.autobattler";
        public const string PluginName = "DD2 Autobattler";
        public const string PluginVersion = "0.1.0";

        internal static Plugin Instance { get; private set; }
        internal static Harmony Harmony { get; private set; }

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> ShadowMode;
        internal static ConfigEntry<bool> LogPreviews;
        internal static ConfigEntry<bool> ShowOverlay;

        public static bool IsAuto
        {
            get { return Enabled != null && Enabled.Value && (ShadowMode == null || !ShadowMode.Value); }
        }

        public static bool IsShadow
        {
            get { return Enabled != null && Enabled.Value && ShadowMode != null && ShadowMode.Value; }
        }

        public static void SetAuto()
        {
            if (Enabled != null)
                Enabled.Value = true;
            if (ShadowMode != null)
                ShadowMode.Value = false;
            Combat.CombatMemory.ClearShadow();
        }

        public static void SetShadow()
        {
            if (Enabled != null)
                Enabled.Value = true;
            if (ShadowMode != null)
                ShadowMode.Value = true;
        }

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("Combat", "Enabled", true,
                "When true, the plugin scores hero turns. Auto plays them; Shadow only logs.");
            ShadowMode = Config.Bind("Combat", "ShadowMode", false,
                "When true with Enabled, you click and the bot logs what it would have clicked. Toggle live from the overlay.");
            LogPreviews = Config.Bind("Logging", "LogPreviews", true,
                "Include per-action preview scores in the JSONL log.");
            ShowOverlay = Config.Bind("UI", "ShowOverlay", true,
                "Show AUTO/SHADOW toggles and the last decision on screen.");

            DecisionLog.Init(Paths.BepInExRootPath, Logger);
            Harmony = new Harmony(PluginGuid);
            InputControllerPatches.Apply(Harmony);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Combat auto={IsAuto} shadow={IsShadow}.");
        }

        private void Start()
        {
            BattleLifecycle.Subscribe();
            DecisionOverlay.Ensure();
        }

        private void OnDestroy()
        {
            BattleLifecycle.Unsubscribe();
            Harmony?.UnpatchSelf();
        }
    }
}
