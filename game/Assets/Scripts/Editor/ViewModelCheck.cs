using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace Game.Editor
{
    /// <summary>
    /// Prints what the viewmodel FBX actually became inside Unity: every node, whether it is active,
    /// what mesh and material it carries and where its box lands relative to the prefab root. A hand
    /// that vanished off screen has four plausible causes (a lost bone parent, an empty mesh, a
    /// disabled renderer, a material the importer dropped) and this tells them apart in one run.
    /// </summary>
    public static class ViewModelCheck
    {
        private const string Path = "Assets/Art/ViewModel/CSSArmsArmed.fbx";

        [MenuItem("Game/Dump ViewModel Import")]
        public static void Dump()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Path);
            if (prefab == null)
            {
                Debug.LogError($"[ViewModelCheck] {Path} missing");
                return;
            }

            var names = string.Join(",", System.Array.ConvertAll(
                prefab.GetComponents<Component>(), c => c.GetType().Name));
            Debug.Log($"[ViewModelCheck] {Path} rootScale={prefab.transform.localScale} " +
                      $"components={names}");

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            var root = instance.transform;
            foreach (var transform in instance.GetComponentsInChildren<Transform>(true))
            {
                var local = transform.localPosition;
                var world = transform.position;
                var line = $"[ViewModelCheck] node='{transform.name}' parent='{(transform.parent != null ? transform.parent.name : "<none>")}' " +
                           $"active={transform.gameObject.activeSelf} " +
                           $"local={Fmt(local)} world={Fmt(world)} scale={Fmt(transform.localScale)}";

                var renderer = transform.GetComponent<Renderer>();
                if (renderer != null)
                {
                    var mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                    var material = renderer.sharedMaterial;
                    var box = renderer.bounds;
                    var relative = box.center - root.position;
                    line += $" | renderer enabled={renderer.enabled} type={renderer.GetType().Name} " +
                            $"verts={(mesh != null ? mesh.vertexCount : -1)} meshBounds={Fmt(mesh != null ? mesh.bounds.size : Vector3.zero)} " +
                            $"mat='{(material != null ? material.name : "<null>")}' shader='{(material != null && material.shader != null ? material.shader.name : "<null>")}' " +
                            $"worldBox={Fmt(box.size)} awayFromRoot={Fmt(relative)}";
                }
                Debug.Log(line);
            }

            Object.DestroyImmediate(instance);
        }

        /// <summary>
        /// The same walk over the built scene, where the answer actually lives: the prefab can import
        /// perfectly and still be parked outside the viewmodel camera's frustum, on a layer that camera
        /// does not draw, or behind a near clip plane the rifle now cuts through.
        /// </summary>
        [MenuItem("Game/Dump Scene ViewModel")]
        public static void DumpScene()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/Main.unity", OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError("[ViewModelCheck] Main.unity did not open");
                return;
            }

            var main = Camera.main;
            Debug.Log($"[ViewModelCheck] scene='{scene.name}' mainCam={Fmt(main != null ? main.transform.position : Vector3.zero)} " +
                      $"near={(main != null ? main.nearClipPlane : 0f):F3} far={(main != null ? main.farClipPlane : 0f):F1}");

            var viewModels = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var transform in viewModels)
            {
                if (transform.name != "ViewModel" && transform.name != "ViewModel Camera") continue;
                Debug.Log($"[ViewModelCheck] '{transform.name}' active={transform.gameObject.activeSelf} " +
                          $"activeInHierarchy={transform.gameObject.activeInHierarchy} " +
                          $"layer={transform.gameObject.layer} pos={Fmt(transform.position)} " +
                          $"rot={Fmt(transform.eulerAngles)} scale={Fmt(transform.lossyScale)}");

                var camera = transform.GetComponent<Camera>();
                if (camera != null)
                    Debug.Log($"[ViewModelCheck]   camera enabled={camera.enabled} clear={camera.clearFlags} " +
                              $"cull=0x{camera.cullingMask:X8} near={camera.nearClipPlane:F3} far={camera.farClipPlane:F2} " +
                              $"fov={camera.fieldOfView:F1} depth={camera.depth} rect={camera.rect}");

                foreach (var renderer in transform.GetComponentsInChildren<Renderer>(true))
                {
                    // Measured off the vertices rather than Renderer.bounds: in a headless build that
                    // AABB is only refreshed by the culling update, so it can report the box the part
                    // had before it was parented and turned.
                    var box = WorldBoxOf(renderer);
                    var toCam = box.center - (main != null ? main.transform.position : transform.position);
                    var line = $"[ViewModelCheck]   part='{renderer.name}' layer={renderer.gameObject.layer} " +
                              $"enabled={renderer.enabled} active={renderer.gameObject.activeInHierarchy} " +
                              $"box={Fmt(box.size)} fromEye={Fmt(toCam)} dist={toCam.magnitude:F3}m " +
                              $"path='{Ancestry(renderer.transform)}' " +
                              $"mat='{(renderer.sharedMaterial != null ? renderer.sharedMaterial.name : "<null>")}'";

                    if (main != null)
                    {
                        var viewport = main.WorldToViewportPoint(box.center);
                        var span = ViewportSpanOf(renderer, main);
                        line += $" viewport=({viewport.x:F2}, {viewport.y:F2}, {viewport.z:F3}) " +
                              $"screenX={span.x:F2}..{span.y:F2} screenY={span.z:F2}..{span.w:F2} " +
                              $"onScreen={OnScreenFractionOf(renderer, main):P0}";
                    }
                    Debug.Log(line);
                }
            }
        }

        /// <summary>The mesh's eight corners pushed through the node's world matrix.</summary>
        private static Bounds WorldBoxOf(Renderer renderer)
        {
            var corners = WorldCornersOf(renderer);
            if (corners == null) return renderer.bounds;

            var box = new Bounds(corners[0], Vector3.zero);
            for (var i = 1; i < corners.Length; i++) box.Encapsulate(corners[i]);
            return box;
        }

        /// <summary>The camera's own viewport rect for a part: a box centred at (0.72, -0.13) says
        /// nothing about how much of it is on screen, and that is the number this check exists for.</summary>
        private static Vector4 ViewportSpanOf(Renderer renderer, Camera camera)
        {
            var corners = WorldCornersOf(renderer);
            if (corners == null) return Vector4.zero;

            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var corner in corners)
            {
                var v = camera.WorldToViewportPoint(corner);
                min = Vector2.Min(min, new Vector2(v.x, v.y));
                max = Vector2.Max(max, new Vector2(v.x, v.y));
            }
            return new Vector4(min.x, max.x, min.y, max.y);
        }

        /// <summary>Share of a part's vertices that land inside the frame. Vertices rather than the box:
        /// the forearms trail behind the eye plane, so the box always overlaps the camera while the skin
        /// that matters can still be entirely off screen.</summary>
        private static float OnScreenFractionOf(Renderer renderer, Camera camera)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return -1f;

            var matrix = renderer.transform.localToWorldMatrix;
            var near = camera.nearClipPlane;
            var vertices = mesh.vertices;
            var stride = Mathf.Max(1, vertices.Length / 256);
            int inside = 0, total = 0;
            for (var i = 0; i < vertices.Length; i += stride)
            {
                var v = camera.WorldToViewportPoint(matrix.MultiplyPoint(vertices[i]));
                total++;
                if (v.z > near && v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f) inside++;
            }
            return total == 0 ? 0f : (float)inside / total;
        }

        private static Vector3[] WorldCornersOf(Renderer renderer)
        {
            var filter = renderer.GetComponent<MeshFilter>();
            var mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null) return null;

            var matrix = renderer.transform.localToWorldMatrix;
            var corners = new Vector3[8];
            for (var i = 0; i < 8; i++)
                corners[i] = matrix.MultiplyPoint(new Vector3(
                    (i & 1) == 0 ? mesh.bounds.min.x : mesh.bounds.max.x,
                    (i & 2) == 0 ? mesh.bounds.min.y : mesh.bounds.max.y,
                    (i & 4) == 0 ? mesh.bounds.min.z : mesh.bounds.max.z));
            return corners;
        }

        private static string Ancestry(Transform node)
        {
            var parts = new System.Collections.Generic.List<string>();
            for (var current = node; current != null; current = current.parent)
                parts.Add($"{current.name}{(current.localRotation == Quaternion.identity ? "" : $"@{Fmt(current.eulerAngles)}")}");
            parts.Reverse();
            return string.Join(" > ", parts);
        }

        private static string Fmt(Vector3 value) =>
            $"({value.x:F3}, {value.y:F3}, {value.z:F3})";
    }
}
