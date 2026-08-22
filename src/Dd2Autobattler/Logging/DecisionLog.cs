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
        private static string _runDir;
        private static string _jsonlPath;
        private static string _fightId = "none";
        private static int _turnIndex;

        public static string LastSummary { get; private set; } = "DD2 Autobattler idle";

        public static void Init(string bepInExRoot, ManualLogSource log)
        {
            _log = log;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            _runDir = Path.Combine(bepInExRoot, "Dd2Autobattler", "logs", stamp);
            Directory.CreateDirectory(_runDir);
            _jsonlPath = Path.Combine(_runDir, "decisions.jsonl");
            WriteLine(new JObject
            {
                ["type"] = "session_start",
                ["utc"] = DateTime.UtcNow.ToString("o"),
                ["path"] = _jsonlPath
            });
            _log.LogInfo($"Decision log: {_jsonlPath}");
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
            SetSummary($"Fight start: {_fightId}");
        }

        public static void EndFight(JObject extra)
        {
            var obj = extra ?? new JObject();
            obj["type"] = "fight_end";
            obj["fight"] = _fightId;
            obj["turns"] = _turnIndex;
            obj["utc"] = DateTime.UtcNow.ToString("o");
            WriteLine(obj);
            SetSummary($"Fight end: {_fightId}");
        }

        public static void Turn(JObject record, string summary)
        {
            _turnIndex++;
            record["type"] = "turn";
            record["fight"] = _fightId;
            record["turn_index"] = _turnIndex;
            record["utc"] = DateTime.UtcNow.ToString("o");
            WriteLine(record);
            SetSummary(summary);
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
                ["message"] = message,
                ["exception"] = ex != null ? ex.GetType().Name : null,
                ["detail"] = ex != null ? ex.Message : null
            };
            WriteLine(obj);
        }

        private static void SetSummary(string summary)
        {
            LastSummary = summary ?? "";
            _log?.LogInfo(summary);
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
