using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;

namespace Dd2GottaGoFast
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "drednot.dd2.gottagofast";
        public const string PluginName = "DD2 Gotta Go Fast";
        public const string PluginVersion = "1.0.0";

        internal static Harmony Harmony;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> DriveMultiplier;

        private void Awake()
        {
            Enabled = Config.Bind("Driving", "Enabled", true,
                "Force the game's fast_driving editor pref on.");
            DriveMultiplier = Config.Bind("Driving", "Multiplier", 20f,
                "Stagecoach speed multiplier. Game default fast-drive is 5. 20 matches the SpeeeeeedWagon file.");

            Harmony = new Harmony(PluginGuid);
            var cmd = AccessTools.TypeByName("Assets.Code.Utils.CommandLineUtils");
            var isEnabled = cmd == null ? null : AccessTools.Method(cmd, "IsEditorPrefsEnabled");
            if (isEnabled == null)
            {
                Logger.LogError("CommandLineUtils.IsEditorPrefsEnabled not found; launch with -allowEditorPrefs as fallback.");
            }
            else
            {
                Harmony.Patch(isEnabled, prefix: new HarmonyMethod(typeof(Plugin), nameof(ForceEditorPrefsEnabled)));
                Logger.LogInfo("Patched IsEditorPrefsEnabled.");
            }

            ApplyDrivingPrefs();
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. driving={Enabled.Value} mult={DriveMultiplier.Value}");
        }

        private void Start()
        {
            ApplyDrivingPrefs();
        }

        private void OnDestroy()
        {
            Harmony?.UnpatchSelf();
        }

        private static bool ForceEditorPrefsEnabled(ref bool __result)
        {
            __result = true;
            return false;
        }

        private void ApplyDrivingPrefs()
        {
            if (Enabled == null || !Enabled.Value)
                return;

            var prefsType = AccessTools.TypeByName("Assets.Code.Utils.TextBasedEditorPrefs");
            var baseType = AccessTools.TypeByName("Assets.Code.Utils.TextBasedEditorPrefsBaseType");
            if (prefsType == null || baseType == null)
            {
                Logger.LogError("TextBasedEditorPrefs types not found.");
                return;
            }

            var fastDriving = AccessTools.Field(baseType, "FAST_DRIVING")?.GetValue(null);
            var fastMult = AccessTools.Field(baseType, "FAST_DRIVING_MULTIPLIER")?.GetValue(null);
            if (fastDriving == null || fastMult == null)
            {
                Logger.LogError("FAST_DRIVING fields not found.");
                return;
            }

            var setBool = AccessTools.Method(prefsType, "SetBool", new[] { fastDriving.GetType(), typeof(bool) });
            var setFloat = AccessTools.Method(prefsType, "SetFloat", new[] { fastMult.GetType(), typeof(float) });
            if (setBool == null || setFloat == null)
            {
                Logger.LogError("SetBool/SetFloat not found.");
                return;
            }

            setBool.Invoke(null, new object[] { fastDriving, true });
            setFloat.Invoke(null, new object[] { fastMult, DriveMultiplier.Value });
            Logger.LogInfo($"Applied fast_driving with multiplier {DriveMultiplier.Value}.");
        }
    }
}
