using System;
using UnityEngine;

namespace Game.UI
{
    public static class UiAssets
    {
        private static Font _default;

        /// <summary>
        /// Uses the built-in legacy runtime font and falls back to an OS font, so the UI needs no
        /// TextMeshPro essentials import to render.
        /// </summary>
        public static Font DefaultFont
        {
            get
            {
                if (_default != null) return _default;

                try
                {
                    _default = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                }
                catch (Exception e)
                {
                    Debug.LogWarning("LegacyRuntime font unavailable: " + e.Message);
                }

                if (_default == null)
                    _default = Font.CreateDynamicFontFromOSFont(
                        new[] { "Segoe UI", "Arial", "Tahoma" }, 32);

                return _default;
            }
        }
    }
}
