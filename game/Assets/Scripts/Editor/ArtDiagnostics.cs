using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Prints what the imported character/animation FBX files actually contain (bones, clips,
    /// materials), so the animator wiring is written against real names instead of guesses.
    ///
    ///   Unity.exe -batchmode -executeMethod Game.Editor.ArtDiagnostics.Report -quit -logFile art.log
    /// </summary>
    public static class ArtDiagnostics
    {
        private const string ReportTag = "[ArtDiag]";
        private const int BonesToPrint = 40;

        [MenuItem("Game/Art Diagnostics Report")]
        public static void Report()
        {
            var guids = AssetDatabase.FindAssets("t:GameObject",
                new[] { "Assets/Art/Characters", "Assets/Art/Animations" });

            foreach (var guid in guids.OrderBy(g => AssetDatabase.GUIDToAssetPath(g)))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                var clips = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().ToList();
                var renderer = prefab.GetComponentInChildren<Renderer>();
                var materials = prefab.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials).Distinct().ToList();

                Log($"{path}");
                Log($"  animationType={importer?.animationType.ToString() ?? "n/a"} " +
                    $"avatar={prefab.GetComponent<Avatar>() != null} " +
                    $"skinned={prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length} " +
                    $"meshFilters={prefab.GetComponentsInChildren<MeshFilter>(true).Length} " +
                    $"clips={clips.Count}");
                Log($"  renderer={(renderer == null ? "none" : renderer.GetType().Name + " scale=" + ScaleOf(renderer))} " +
                    $"materials=[{string.Join(", ", materials.Select(m => m == null ? "null" : m.name))}]" +
                    $"textures=[{string.Join(", ", materials.SelectMany(m => m == null ? Enumerable.Empty<Texture>() : TexturesOf(m)).Distinct().Select(t => t.name))}]");

                var bones = prefab.GetComponentsInChildren<Transform>(true)
                    .Select(t => t.name).Distinct().ToList();
                Log($"  nodes({bones.Count})=[{string.Join(", ", bones.Take(BonesToPrint))}" +
                    $"{(bones.Count > BonesToPrint ? ", ..." : "")}]");

                if (clips.Count > 0)
                    Log($"  clipNames=[{string.Join(", ", clips.Select(c => c.name).Take(60))}" +
                        $"{(clips.Count > 60 ? ", ..." : "")}]");
            }

            Log("done");
        }

        private static IEnumerable<Texture> TexturesOf(Material material)
        {
            var uv = new List<Texture>();
            var shader = material.shader;
            if (shader == null) yield break;

            for (int i = 0; i < ShaderUtil.GetPropertyCount(shader); i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) != ShaderUtil.ShaderPropertyType.TexEnv) continue;
                var name = ShaderUtil.GetPropertyName(shader, i);
                var texture = material.GetTexture(name);
                if (texture != null) uv.Add(texture);
            }

            foreach (var t in uv) yield return t;
        }

        private static string ScaleOf(Renderer renderer)
        {
            var lossy = renderer.transform.lossyScale;
            var mesh = renderer is SkinnedMeshRenderer smr ? smr.sharedMesh
                : renderer.GetComponent<MeshFilter>()?.sharedMesh;
            var size = mesh != null ? mesh.bounds.size : Vector3.zero;
            return $"({(size.x * lossy.x):F2}x{(size.y * lossy.y):F2}x{(size.z * lossy.z):F2})";
        }

        private static void Log(string message) => Debug.Log($"{ReportTag} {message}");
    }
}
