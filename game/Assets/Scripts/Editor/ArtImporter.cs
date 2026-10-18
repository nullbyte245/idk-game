using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Configures the imported CC0 characters for Generic (they carry their own clips, and the
    /// Humanoid auto-mapper does not produce a valid avatar for these armatures), then reports whether
    /// a clip taken from one model can drive another: Generic clips bind by transform path, so the two
    /// hierarchies have to agree.
    ///
    ///   Unity.exe -batchmode -executeMethod Game.Editor.ArtImporter.Configure -quit -logFile art.log
    /// </summary>
    public static class ArtImporter
    {
        private const string ReportTag = "[ArtImport]";
        private const string CharacterPath = "Assets/Art/Characters/SasAgent.fbx";
        private const string HumanPath = "Assets/Art/Characters/AnimatedHuman.fbx";

        private static readonly string[] ModelPaths =
        {
            CharacterPath, HumanPath,
            "Assets/Art/Characters/Warrior.fbx",
            "Assets/Art/Characters/Ranger.fbx",
            "Assets/Art/Characters/Wizard.fbx",
            "Assets/Art/Characters/Warrior_Sword.fbx"
        };

        // Props carry no rig worth avataring: the AK is a bone-parented child of the player's hand
        // and the viewmodel hands are static, so an avatar on either would only confuse the animator.
        private static readonly string[] PropPaths =
        {
            "Assets/Art/Weapons/AK47.fbx",
            "Assets/Art/Weapons/GunMount.fbx",
            "Assets/Art/ViewModel/CSSArmsArmed.fbx"
        };

        private static readonly string[] LoopingClips =
        {
            "Idle", "Idle_Weapon", "Walk", "Run", "Run_Weapon"
        };

        [MenuItem("Game/Configure Art Import Settings")]
        public static void Configure()
        {
            foreach (var path in ModelPaths)
            {
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                {
                    Log($"MISSING {path}");
                    continue;
                }

                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
                var loops = ConfigureClips(importer, path);
                importer.SaveAndReimport();
                Report(path, loops);
            }

            CompareBindings(CharacterPath, HumanPath);
            ConfigureProps();
            Log("done");
        }

        /// <summary>
        /// Props import as plain meshes: Generic animation type but no avatar, and no clip config,
        /// so Unity leaves their single bind pose alone instead of generating an Animator-ready rig.
        /// </summary>
        private static void ConfigureProps()
        {
            foreach (var path in PropPaths)
            {
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null)
                {
                    Log($"MISSING {path}");
                    continue;
                }

                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
                importer.materialLocation = ModelImporterMaterialLocation.InPrefab;
                importer.SaveAndReimport();
                Report(path, "none");
            }
        }

        /// <summary>
        /// FBX takes import with Loop Time off, so Idle plays for one second and then holds its last
        /// frame - the model looks frozen. Locomotion clips are marked looping here, and every clip has
        /// its root translation/rotation baked into the pose so the animation never drags the body
        /// across the ground on its own; the CharacterController owns movement.
        /// </summary>
        private static string ConfigureClips(ModelImporter importer, string path)
        {
            var clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0) clips = importer.defaultClipAnimations;

            var looped = 0;
            for (var i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];
                var shortName = (clip.name ?? string.Empty).Split('|').Last();
                var isLoop = LoopingClips.Contains(shortName, StringComparer.OrdinalIgnoreCase);

                clip.loop = isLoop;
                clip.loopTime = isLoop;
                clip.wrapMode = isLoop ? WrapMode.Loop : WrapMode.ClampForever;
                clip.lockRootPositionXZ = true;
                clip.lockRootRotation = true;
                clip.keepOriginalOrientation = true;

                clips[i] = clip;
                if (isLoop) looped++;
            }

            importer.clipAnimations = clips;
            var names = string.Join(", ", clips.Select(c => $"{c.name.Split('|').Last()}{(c.loop ? "*" : "")}"));
            Log($"  {System.IO.Path.GetFileName(path)} clips=[{names}] looped={looped}");
            return names;
        }

        private static void Report(string path, string clipNames)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) { Log($"NO PREFAB {path}"); return; }

            var clips = ClipsOf(prefab);
            var bounds = BoundsOf(prefab, out var materialNames, out var textureNames);

            Log($"{path}");
            Log($"  clips({clips.Count})=[{string.Join(", ", clips.Select(c => $"{c.name}:{c.wrapMode}"))}]");
            Log($"  importerClips=[{clipNames}]");
            Log($"  size={bounds.size.x:F2}x{bounds.size.y:F2}x{bounds.size.z:F2} " +
                $"materials=[{string.Join(", ", materialNames)}] textures=[{string.Join(", ", textureNames)}]");

            // The proof that the pack's textures travelled with the model rather than only sitting in
            // Assets/Art/Textures: embedded textures are Texture2D sub-assets of the FBX itself.
            var embedded = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Texture2D>().ToList();
            Log($"  embeddedTextures({embedded.Count})=" +
                $"[{string.Join(", ", embedded.Select(t => $"{t.name} {t.width}x{t.height} {t.graphicsFormat}"))}]");
        }

        private static void CompareBindings(string targetPath, string donorPath)
        {
            var target = PathsOf(AssetDatabase.LoadAssetAtPath<GameObject>(targetPath));
            var donor = PathsOf(AssetDatabase.LoadAssetAtPath<GameObject>(donorPath));

            var shared = target.Intersect(donor).ToList();
            var missing = donor.Except(shared).ToList();

            Log($"binding check: targetNodes={target.Count} donorNodes={donor.Count} " +
                $"shared={shared.Count} donorOnly={missing.Count}");
            Log($"  donorOnly=[{string.Join(", ", missing.Take(25))}]");
        }

        private static List<string> PathsOf(GameObject prefab)
        {
            var paths = new List<string>();
            if (prefab == null) return paths;

            foreach (var transform in prefab.GetComponentsInChildren<Transform>(true))
            {
                var relative = AnimationUtility.CalculateTransformPath(transform, prefab.transform);
                paths.Add(relative);
            }

            paths.Sort();
            return paths;
        }

        private static List<AnimationClip> ClipsOf(GameObject prefab) =>
            AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(prefab))
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .OrderBy(c => c.name)
                .ToList();

        private static Bounds BoundsOf(GameObject prefab, out List<string> materialNames, out List<string> textureNames)
        {
            var center = Vector3.zero;
            var size = Vector3.zero;
            materialNames = new List<string>();
            textureNames = new List<string>();

            var renderers = prefab.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                center += renderer.bounds.center;
                size = Vector3.Max(size, renderer.bounds.size);

                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null) continue;
                    materialNames.Add(material.name);
                    textureNames.AddRange(MaterialTextures(material).Select(t => t.name));
                }
            }

            if (renderers.Length > 0) center /= renderers.Length;
            return new Bounds(center, size);
        }

        private static readonly string[] TextureSlots = { "_MainTex", "_BaseMap", "_BumpMap", "_EmissionMap" };

        private static List<Texture> MaterialTextures(Material material)
        {
            var found = new List<Texture>();

            // Probing the well-known slots directly keeps the check working even when the shader's
            // property list is not available in a -nographics batch run.
            foreach (var slot in TextureSlots)
            {
                var direct = material.GetTexture(slot);
                if (direct != null) found.Add(direct);
            }

            var shader = material.shader;
            if (shader == null) return found;

            for (var i = 0; i < ShaderUtil.GetPropertyCount(shader); i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var texture = material.GetTexture(ShaderUtil.GetPropertyName(shader, i));
                if (texture != null) found.Add(texture);
            }

            return found;
        }

        private static void Log(string message) => Debug.Log($"{ReportTag} {message}");
    }
}
