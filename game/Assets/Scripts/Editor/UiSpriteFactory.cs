using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Game.Editor
{
    /// <summary>
    /// Renders the rounded rectangle sprites the whole UI is drawn from.
    ///
    /// Every panel, button, row and bar used to be a sprite-less Image, which uGUI fills with two
    /// triangles - hard 90 degree corners and stair-stepped diagonals at any size. A 9-slice sprite
    /// fixes both: the corners are baked once at high resolution with per-pixel coverage, and the
    /// straight edges are stretched, so the radius stays identical on a 44 px join button and an
    /// 1040 px lobby panel instead of scaling with the widget.
    ///
    /// Corner coverage is analytic (distance to the bounding rounded rect) rather than a box filter,
    /// so the arc edge gets true fractional alpha instead of a blurry halo.
    /// </summary>
    public static class UiSpriteFactory
    {
        private const string SpriteFolder = "Assets/Art/UI/Sprites";

        /// <summary>Texture pixels per canvas unit: corners are baked 6x larger than they are drawn.</summary>
        private const int Supersample = 6;

        /// <summary>
        /// Image divides the sprite border by sprite.pixelsPerUnit / canvas.referencePixelsPerUnit
        /// (uGUI Image.cs:1064), so a corner baked at Supersample px per canvas unit needs that many
        /// times the reference 100 to still measure exactly <see cref="PanelRadius"/> units.
        /// </summary>
        private const float PixelsPerUnit = 100f * Supersample;

        /// <summary>Solid middle, in canvas units, that 9-slicing stretches instead of resampling.</summary>
        private const int MiddleUnits = 4;

        public const string PanelName = "UiPanel";
        public const string ChipName = "UiChip";
        public const string PillName = "UiPill";

        public const float PanelRadius = 14f;
        public const float ChipRadius = 8f;
        public const float PillRadius = 10f;

        private static readonly (string Name, float Radius)[] Specs =
        {
            (PanelName, PanelRadius),
            (ChipName, ChipRadius),
            (PillName, PillRadius),
        };

        private static Sprite _panel;
        private static Sprite _chip;
        private static Sprite _pill;

        public static Sprite PanelSprite => _panel;
        public static Sprite ChipSprite => _chip;
        public static Sprite PillSprite => _pill;

        /// <summary>
        /// BuildAll calls this before the UI hierarchy exists: the sprites have to be on disk and
        /// imported before any Image can point at them.
        /// </summary>
        public static void Prepare()
        {
            EnsureFolder();
            foreach (var spec in Specs) Write(spec.Name, spec.Radius);
            AssetDatabase.SaveAssets();

            _panel = Load(PanelName);
            _chip = Load(ChipName);
            _pill = Load(PillName);
            if (_panel == null || _chip == null || _pill == null)
                Debug.LogWarning("[UiSpriteFactory] a rounded sprite is missing; those Images fall back to squares.");
            else
            {
                // Logged so a headless run can prove the slice math: Image turns border pixels into
                // border units by way of pixelsPerUnit, so rect/ppu/border together predict the radius.
                Debug.Log($"[UiSpriteFactory] rounded sprites ready in {SpriteFolder}: " +
                          $"{PanelName} r{PanelRadius}, {ChipName} r{ChipRadius}, {PillName} r{PillRadius}; " +
                          $"panel {_panel.rect.width}x{_panel.rect.height}px at ppu {_panel.pixelsPerUnit}, " +
                          $"border {_panel.border.x} -> {_panel.border.x * 100f / _panel.pixelsPerUnit} units");
            }
        }

        [MenuItem("Game/Build UI Sprites")]
        public static void BuildSprites() => Prepare();

        private static void EnsureFolder()
        {
            if (AssetDatabase.IsValidFolder(SpriteFolder)) return;
            if (!AssetDatabase.IsValidFolder("Assets/Art"))
                AssetDatabase.CreateFolder("Assets", "Art");
            if (!AssetDatabase.IsValidFolder("Assets/Art/UI"))
            {
                var ui = AssetDatabase.CreateFolder("Assets/Art", "UI");
                AssetDatabase.CreateFolder(ui, "Sprites");
                return;
            }
            AssetDatabase.CreateFolder("Assets/Art/UI", "Sprites");
        }

        private static void Write(string name, float radius)
        {
            var border = Mathf.CeilToInt(radius * Supersample);
            var size = border * 2 + MiddleUnits * Supersample;
            var px = new Color32[size * size];

            // The shape is a rounded rect bounding the whole texture, so its corner arc radius equals
            // the 9-slice border and the straight inset is half the texture minus that.
            float half = size * 0.5f;
            float inner = half - border;

            // The alpha ramp has to be a whole canvas unit wide, not one texture pixel: the corner is
            // later minified by however many texels fit in a screen pixel, and a sub-unit ramp collapses
            // back into the hard staircase it was supposed to remove.
            float ramp = Supersample;

            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - half) - inner;
                    float dy = Mathf.Abs(y + 0.5f - half) - inner;
                    float ox = Mathf.Max(dx, 0f);
                    float oy = Mathf.Max(dy, 0f);
                    float dist = Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(dx, dy), 0f) - border;

                    var a = Mathf.Clamp01(0.5f - dist / ramp);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.SetPixels32(px);
            texture.Apply(false, false);

            var path = SpriteFolder + "/" + name + ".png";
            File.WriteAllBytes(path, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"[UiSpriteFactory] {path} has no importer; UI will fall back to squares.");
                return;
            }

            // spriteBorder is authored in texture pixels but Image reads it back in canvas units, so
            // pixelsPerUnit is what turns an 88 px corner into the 22 unit corner the panel wants.
            var spriteBorder = new Vector4(border, border, border, border);
            if (importer.textureType != TextureImporterType.Sprite
                || importer.spriteImportMode != SpriteImportMode.Single
                || !Mathf.Approximately(importer.spritePixelsPerUnit, PixelsPerUnit)
                || importer.spriteBorder != spriteBorder
                || importer.mipmapEnabled
                || !importer.alphaIsTransparency
                || importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = PixelsPerUnit;
                importer.spriteBorder = spriteBorder;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = true;
                importer.mipmapEnabled = false;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.filterMode = FilterMode.Bilinear;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
        }

        private static Sprite Load(string name)
        {
            return AssetDatabase.LoadAssetAtPath<Sprite>(SpriteFolder + "/" + name + ".png");
        }
    }
}
