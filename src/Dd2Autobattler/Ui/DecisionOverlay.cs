using Dd2Autobattler.Logging;
using UnityEngine;

namespace Dd2Autobattler.Ui
{
    public sealed class DecisionOverlay : MonoBehaviour
    {
        private static DecisionOverlay _instance;
        private GUIStyle _style;

        public static void Ensure()
        {
            if (_instance != null)
                return;
            var go = new GameObject("Dd2AutobattlerOverlay");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<DecisionOverlay>();
        }

        private void OnGUI()
        {
            if (Plugin.ShowOverlay == null || !Plugin.ShowOverlay.Value)
                return;

            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 16,
                    normal = { textColor = Color.white },
                    wordWrap = true
                };
            }

            var text = DecisionLog.LastSummary ?? "";
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(8, 8, 900, 28), Texture2D.whiteTexture);
            GUI.color = Color.white;
            GUI.Label(new Rect(12, 10, 890, 24), text, _style);
        }
    }
}
