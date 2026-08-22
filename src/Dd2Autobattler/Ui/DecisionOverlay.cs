using Dd2Autobattler.Logging;
using UnityEngine;

namespace Dd2Autobattler.Ui
{
    public sealed class DecisionOverlay : MonoBehaviour
    {
        private static DecisionOverlay _instance;
        private GUIStyle _style;
        private GUIStyle _onStyle;
        private GUIStyle _offStyle;

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
                _onStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold
                };
                _offStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 13
                };
            }

            var text = DecisionLog.LastSummary ?? "";
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(new Rect(8, 8, 980, 32), Texture2D.whiteTexture);
            GUI.color = Color.white;

            var auto = Plugin.IsAuto;
            var shadow = Plugin.IsShadow;
            if (GUI.Button(new Rect(12, 10, 64, 24), "AUTO", auto ? _onStyle : _offStyle) && !auto)
                Plugin.SetAuto();
            if (GUI.Button(new Rect(80, 10, 84, 24), "SHADOW", shadow ? _onStyle : _offStyle) && !shadow)
                Plugin.SetShadow();
            GUI.Label(new Rect(172, 10, 808, 24), text, _style);
        }
    }
}
