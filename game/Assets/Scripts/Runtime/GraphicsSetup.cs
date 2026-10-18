using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// Applies the high quality render settings before the first scene loads, so the Editor play
    /// mode and the built player both start with MSAA and full-resolution shadows.
    /// </summary>
    public static class GraphicsSetup
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplyQuality()
        {
            // The runtime clamps MSAA to what the display/driver actually supports.
            QualitySettings.antiAliasing = 8;
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.shadowResolution = ShadowResolution.High;
            QualitySettings.shadowDistance = 150f;
            // Four cascades keep the near contact shadows crisp instead of the two-cascade blur,
            // which is what sells the occlusion the post pass adds.
            QualitySettings.shadowCascades = 4;
            QualitySettings.shadowCascade4Split = new Vector3(0.04f, 0.14f, 0.32f);
            QualitySettings.pixelLightCount = 6;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            QualitySettings.skinWeights = SkinWeights.FourBones;
            QualitySettings.lodBias = 2f;
            QualitySettings.masterTextureLimit = 0;
            QualitySettings.vSyncCount = 1;
        }
    }
}
