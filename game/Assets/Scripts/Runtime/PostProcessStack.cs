using UnityEngine;

namespace Game.Runtime
{
    /// <summary>
    /// Runs the whole colour grade chain as image effects on one camera: bloom, screen space
    /// occlusion from the depth texture, ACES tone mapping, saturation/contrast, vignette, fringing
    /// and film grain. All of it lives in Hidden/NeonRelay/PostFX and is driven by a material asset,
    /// because a runtime Shader.Find would lose the shader to the build's strip step.
    ///
    /// Only the world camera carries this. The viewmodel camera draws afterwards with a depth clear
    /// and no effects of its own, which keeps the grade from being applied twice to one pixel.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class PostProcessStack : MonoBehaviour
    {
        [SerializeField] private Material postFx;

        [Header("Bloom")]
        [SerializeField] private bool bloomEnabled = true;
        // Well above 1.0 on purpose: the bright pass works in linear light, so a low threshold lets the
        // sky and every sun-lit wall through, and the whole arena starts to glow. This value only lets
        // the sun disc itself past.
        [SerializeField] private float threshold = 1.7f;
        [SerializeField] private float knee = 0.4f;
        [SerializeField] private float strength = 0.42f;
        [SerializeField] private float spread = 1.15f;
        [Range(1, 4)] [SerializeField] private int iterations = 2;

        [Header("Grade")]
        // ACES rolls the midtones down hard, so exposure has to over-compensate or a sunlit arena
        // reads as dusk.
        [SerializeField] private float exposure = 1.24f;
        [SerializeField] private float contrast = 1.05f;
        [SerializeField] private float saturation = 1.06f;
        [SerializeField] private float vignette = 0.2f;
        [SerializeField] private float grain = 0.012f;
        [SerializeField] private float chromatic = 0.006f;

        [Header("Occlusion")]
        [SerializeField] private bool ambientOcclusion = true;
        [SerializeField] private float aoStrength = 0.38f;
        [SerializeField] private float aoRadius = 0.28f;
        [SerializeField] private float aoFalloff = 0.5f;

        private const RenderTextureFormat Workspace = RenderTextureFormat.ARGBHalf;
        private static readonly int Threshold = Shader.PropertyToID("_Threshold");
        private static readonly int Knee = Shader.PropertyToID("_Knee");
        private static readonly int BloomStrength = Shader.PropertyToID("_BloomStrength");
        private static readonly int Exposure = Shader.PropertyToID("_Exposure");
        private static readonly int Contrast = Shader.PropertyToID("_Contrast");
        private static readonly int Saturation = Shader.PropertyToID("_Saturation");
        private static readonly int Vignette = Shader.PropertyToID("_Vignette");
        private static readonly int Grain = Shader.PropertyToID("_Grain");
        private static readonly int Chromatic = Shader.PropertyToID("_Chromatic");
        private static readonly int AOStrength = Shader.PropertyToID("_AOStrength");
        private static readonly int AORadius = Shader.PropertyToID("_AORadius");
        private static readonly int AOFalloff = Shader.PropertyToID("_AOFalloff");
        private static readonly int BloomTex = Shader.PropertyToID("_BloomTex");
        private static readonly int BlurDir = Shader.PropertyToID("_BlurDir");
        private static readonly int BlurSpread = Shader.PropertyToID("_BlurSpread");
        private static readonly int AOScale = Shader.PropertyToID("_AOScale");

        private Camera _camera;

        private void OnEnable()
        {
            _camera = GetComponent<Camera>();
            if (_camera != null && ambientOcclusion) _camera.depthTextureMode |= DepthTextureMode.Depth;
        }

        private void OnDisable()
        {
            if (_camera != null) _camera.depthTextureMode &= ~DepthTextureMode.Depth;
        }

        private void OnRenderImage(RenderTexture source, RenderTexture destination)
        {
            if (postFx == null)
            {
                Graphics.Blit(source, destination);
                return;
            }

            postFx.SetFloat(Threshold, threshold);
            postFx.SetFloat(Knee, knee);
            postFx.SetFloat(BloomStrength, strength);
            postFx.SetFloat(Exposure, exposure);
            postFx.SetFloat(Contrast, contrast);
            postFx.SetFloat(Saturation, saturation);
            postFx.SetFloat(Vignette, vignette);
            postFx.SetFloat(Grain, grain);
            postFx.SetFloat(Chromatic, chromatic);

            if (bloomEnabled) ApplyBloom(source, destination);
            else
            {
                postFx.SetTexture(BloomTex, Texture2D.blackTexture);
                Composite(source, destination);
            }
        }

        private void ApplyBloom(RenderTexture source, RenderTexture destination)
        {
            int width = Mathf.Max(2, source.width / 2);
            int height = Mathf.Max(2, source.height / 2);
            var bloom = RenderTexture.GetTemporary(width, height, 0, Workspace, RenderTextureReadWrite.Linear);
            var swap = RenderTexture.GetTemporary(width, height, 0, Workspace, RenderTextureReadWrite.Linear);
            try
            {
                Graphics.Blit(source, bloom, postFx, 0);
                for (var i = 0; i < iterations; i++)
                {
                    // Each round widens the kernel, which is what turns a hard bright edge into a halo.
                    postFx.SetFloat(BlurSpread, spread * (i + 1));
                    postFx.SetVector(BlurDir, new Vector4(1f, 0f, 0f, 0f));
                    Graphics.Blit(bloom, swap, postFx, 1);
                    postFx.SetVector(BlurDir, new Vector4(0f, 1f, 0f, 0f));
                    Graphics.Blit(swap, bloom, postFx, 1);
                }

                postFx.SetTexture(BloomTex, bloom);
                Composite(source, destination);
            }
            finally
            {
                RenderTexture.ReleaseTemporary(bloom);
                RenderTexture.ReleaseTemporary(swap);
            }
        }

        private void Composite(RenderTexture source, RenderTexture destination)
        {
            postFx.SetFloat(AOStrength, ambientOcclusion ? aoStrength : 0f);
            postFx.SetFloat(AORadius, aoRadius);
            postFx.SetFloat(AOFalloff, aoFalloff);
            postFx.SetVector(AOScale, OcclusionScale());
            Graphics.Blit(source, destination, postFx, 2);
        }

        /// <summary>
        /// uv extent of one metre at one metre from the eye. The camera's own projection matrix is
        /// not bound during OnRenderImage, so the field of view has to come from the camera instead.
        /// </summary>
        private Vector2 OcclusionScale()
        {
            var fieldOfView = _camera != null ? _camera.fieldOfView : 60f;
            var halfTan = Mathf.Tan(fieldOfView * 0.5f * Mathf.Deg2Rad);
            var aspect = _camera != null ? _camera.aspect : 1f;
            return new Vector2(1f / (2f * halfTan * aspect), 1f / (2f * halfTan));
        }

        private static int BloomProperty(string name) => Shader.PropertyToID(name);
    }
}
