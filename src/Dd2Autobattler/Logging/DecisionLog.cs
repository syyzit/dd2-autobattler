using System;
using System.Globalization;
using System.IO;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Dd2Autobattler.Logging
{
    public static class DecisionLog
    {
        private static ManualLogSource _log;
        private static string _bepInExRoot;
        private static string _runDir;
        private static string _jsonlPath;
        private static string _fightId = "none";
        private static int _turnIndex;

        /// <summary>JSONL schema. 2 = adds turn snapshots, action_result, fight_end party/outcome.</summary>
        public const int Schema = 2;

        public static string LastSummary { get; private set; } = "DD2 Autobattler idle";
        public static string LastRunnerUp { get; private set; } = "";
        public static string JsonlPath { get { return _jsonlPath; } }
        public static string SessionStamp { get; private set; }
        public static string FightId { get { return _fightId; } }

        public static void Init(string bepInExRoot, ManualLogSource log)
        {
            _log = log;
            _bepInExRoot = bepInExRoot;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            SessionStamp = stamp;
            _runDir = Path.Combine(bepInExRoot, "Dd2Autobattler", "logs", stamp);
            Directory.CreateDirectory(_runDir);
            _jsonlPath = Path.Combine(_runDir, "decisions.jsonl");
            WriteLine(SessionStartRecord(stamp, SafeGameVersion(), DateTime.UtcNow));
            _log.LogInfo($"Decision log: BepInEx/{RelativeLogPath(stamp)} (commit {BuildInfo.GitCommit ?? "?"}{(BuildInfo.GitDirty ? "+dirty" : "")})");
        }

        /// <summary>
        /// The decision log's location relative to the BepInEx folder, with forward slashes.
        /// Players attach decisions.jsonl to bug reports, so no record carries the absolute
        /// path (it holds the Windows user name and the install folder).
        /// </summary>
        internal static string RelativeLogPath(string stamp)
        {
            return "Dd2Autobattler/logs/" + stamp + "/decisions.jsonl";
        }

        internal static JObject SessionStartRecord(string stamp, string gameVersion, DateTime utc)
        {
            return new JObject
            {
                ["type"] = "session_start",
                ["schema"] = Schema,
                ["session"] = stamp,
                ["plugin_version"] = Plugin.PluginVersion,
                ["git_commit"] = BuildInfo.GitCommit,
                ["git_dirty"] = BuildInfo.GitDirty,
                ["game_version"] = gameVersion,
                ["utc"] = utc.ToString("o"),
                ["path"] = RelativeLogPath(stamp)
            };
        }

        /// <summary>
        /// Takes local folders out of free text written to the log (exception messages can
        /// name a file): the BepInEx folder becomes "BepInEx", the user profile "%USERPROFILE%".
        /// </summary>
        internal static string Scrub(string text, string bepInExRoot, string userProfile)
        {
            if (string.IsNullOrEmpty(text))
                return text;
            text = ReplaceFolder(text, bepInExRoot, "BepInEx");
            text = ReplaceFolder(text, userProfile, "%USERPROFILE%");
            return text;
        }

        private static string ReplaceFolder(string text, string folder, string token)
        {
            if (string.IsNullOrEmpty(folder))
                return text;
            folder = folder.TrimEnd('\\', '/');
            if (folder.Length < 3)
                return text;
            var alt = folder.Replace('\\', '/');
            text = ReplaceIgnoreCase(text, folder, token);
            if (alt != folder)
                text = ReplaceIgnoreCase(text, alt, token);
            return text;
        }

        private static string ReplaceIgnoreCase(string text, string find, string token)
        {
            var i = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            while (i >= 0)
            {
                text = text.Substring(0, i) + token + text.Substring(i + find.Length);
                i = text.IndexOf(find, i + token.Length, StringComparison.OrdinalIgnoreCase);
            }
            return text;
        }

        private static string SafeUserProfile()
        {
            try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { return null; }
        }

        /// <summary>Append any record (action_result, fight_end, ...). Adds fight and utc.</summary>
        public static void Record(string type, JObject obj)
        {
            obj = obj ?? new JObject();
            obj["type"] = type;
            if (obj["fight"] == null)
                obj["fight"] = _fightId;
            obj["utc"] = DateTime.UtcNow.ToString("o");
            WriteLine(obj);
        }

        private static string SafeGameVersion()
        {
            try { return UnityEngine.Application.version; } catch { return null; }
        }

        public static void BeginFight(string fightId, JObject extra)
        {
            _fightId = string.IsNullOrEmpty(fightId) ? "fight" : fightId;
            _turnIndex = 0;
            var obj = extra ?? new JObject();
            obj["type"] = "fight_start";
            obj["fight"] = _fightId;
            obj["utc"] = DateTime.UtcNow.ToString("o");
            WriteLine(obj);
            SetSummary($"Fight start: {_fightId}", "");
        }

        public static void EndFight(JObject extra)
        {
            var obj = extra ?? new JObject();
            obj["type"] = "fight_end";
            obj["schema"] = Schema;
            obj["fight"] = _fightId;
            obj["turns"] = _turnIndex;
            obj["utc"] = DateTime.UtcNow.ToString("o");
            WriteLine(obj);
            SetSummary($"Fight end: {_fightId}", "");
        }

        public static void Turn(JObject record, string summary, string runnerUp = null)
        {
            _turnIndex++;
            record["type"] = "turn";
            record["fight"] = _fightId;
            record["turn_index"] = _turnIndex;
            record["utc"] = DateTime.UtcNow.ToString("o");
            WriteLine(record);
            SetSummary(summary, runnerUp);
        }

        public static void ShadowResult(JObject compare)
        {
            var obj = compare ?? new JObject();
            obj["type"] = "shadow_result";
            obj["fight"] = _fightId;
            obj["turn_index"] = _turnIndex;
            obj["utc"] = DateTime.UtcNow.ToString("o");
            WriteLine(obj);

            var match = obj.Value<bool>("match");
            var bot = obj["bot"] as JObject;
            var human = obj["human"] as JObject;
            var botSkill = bot != null ? bot.Value<string>("skill") : "?";
            var humanSkill = human != null ? human.Value<string>("skill") : "?";
            var gap = obj.Value<float>("gap");
            var summary = match
                ? "SHADOW agree " + humanSkill
                : "SHADOW you " + humanSkill + " | bot " + botSkill + " gap " + gap.ToString("0.0");
            SetSummary(summary, "");
        }

        public static void Info(string message)
        {
            _log?.LogInfo(message);
        }

        public static void Warn(string message)
        {
            _log?.LogWarning(message);
        }

        public static void Error(string message, Exception ex = null)
        {
            if (ex != null)
                _log?.LogError($"{message}\n{ex}");
            else
                _log?.LogError(message);

            var obj = new JObject
            {
                ["type"] = "error",
                ["fight"] = _fightId,
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["message"] = Scrub(message, _bepInExRoot, SafeUserProfile()),
                ["exception"] = ex != null ? ex.GetType().Name : null,
                ["detail"] = ex != null ? Scrub(ex.Message, _bepInExRoot, SafeUserProfile()) : null
            };
            WriteLine(obj);
        }

        private static void SetSummary(string summary, string runnerUp = null)
        {
            LastSummary = summary ?? "";
            if (runnerUp != null)
                LastRunnerUp = runnerUp;
            _log?.LogInfo(summary);
            if (!string.IsNullOrEmpty(LastRunnerUp))
                _log?.LogInfo(LastRunnerUp);
        }

        private static void WriteLine(JObject obj)
        {
            if (string.IsNullOrEmpty(_jsonlPath))
                return;
            try
            {
                File.AppendAllText(_jsonlPath, obj.ToString(Formatting.None) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                _log?.LogError($"Failed to write decision log: {ex.Message}");
            }
        }
    }
}
