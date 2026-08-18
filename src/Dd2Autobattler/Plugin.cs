using BepInEx;
using BepInEx.Configuration;
using Dd2Autobattler.Combat;
using Dd2Autobattler.Logging;
using Dd2Autobattler.Ui;
using HarmonyLib;
using UnityEngine;

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
        internal static ConfigEntry<bool> LogPreviews;
        internal static ConfigEntry<bool> ShowOverlay;

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("Combat", "Enabled", true,
                "When true, the plugin picks hero skills. When false, you play combat yourself.");
            LogPreviews = Config.Bind("Logging", "LogPreviews", true,
                "Include per-action preview scores in the JSONL log.");
            ShowOverlay = Config.Bind("UI", "ShowOverlay", true,
                "Show the last decision as an on-screen line.");

            DecisionLog.Init(Paths.BepInExRootPath, Logger);
            Harmony = new Harmony(PluginGuid);
            InputControllerPatches.Apply(Harmony);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Combat auto={Enabled.Value}.");
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
