using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using Game.Data;
using Game.Gameplay;
using Game.Player;
using Game.Runtime;
using Game.Services;
using Game.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
// UnityEditor also defines SettingsService (the editor preferences window); the game one wins here.
using SettingsService = Game.Services.SettingsService;

namespace Game.Editor
{
    /// <summary>
    /// Builds the Main scene and its full UI hierarchy from code, so the scene is reproducible
    /// and diffable instead of being a hand-made artifact.
    ///
    /// Headless run:
    ///   Unity.exe -batchmode -nographics -projectPath &lt;proj&gt; -executeMethod Game.Editor.SceneBuilder.BuildAll -quit -logFile build.log
    /// </summary>
    public static class SceneBuilder
    {
        private const string SceneFolder = "Assets/Scenes";
        private const string ScenePath = SceneFolder + "/Main.unity";
        private const string MaterialFolder = "Assets/Materials";
        private const string ShaderFolder = "Assets/Shaders";
        private const string PostFxShaderPath = ShaderFolder + "/PostFX.shader";
        private const string PostFxMaterialPath = MaterialFolder + "/PostFX.mat";
        // One shader, two materials: _Radial turns the same additive pass into a round blob for the
        // flame and a thin ribbon for the tracer. Both have to be assets or the build strips the shader.
        private const string ShotFxShaderPath = ShaderFolder + "/ShotFx.shader";
        private const string ShotStreakMaterialPath = MaterialFolder + "/ShotStreak.mat";
        private const string ShotGlowMaterialPath = MaterialFolder + "/ShotGlow.mat";
        private const string ShotClipPath = "Assets/sounds/163457__lemudcrab__ak47-shot.wav";
        private const string DataFolder = "Assets/Data";
        private const string ConfigPath = DataFolder + "/GameConfig.asset";

        // Player visuals come from the CC0 pack in Assets/Art; missing files fall back to primitives.
        // SasAgent.fbx is the Sketchfab SAS mesh re-skinned onto that pack's armature by retarget_sas.py.
        private const string BodyPath = "Assets/Art/Characters/SasAgent.fbx";
        private const string TextureFolder = "Assets/Art/Textures";
        private const string AnimatorPath = "Assets/Art/PlayerAnimator.controller";
        private const string IconFolder = "Assets/Art/UI";

        // The rifle is bone-parented in Unity too. Its authored pose is read out of GunMount.fbx,
        // which mount_ak47.py writes by parenting the real AK mesh to the player rig's hand bone.
        private const string GunPath = "Assets/Art/Weapons/AK47.fbx";
        private const string GunMountPath = "Assets/Art/Weapons/GunMount.fbx";
        private const string GunMeshName = "AK47_CS2";
        private const string HandBoneName = "Fist.R";
        private const string ViewModelPath = "Assets/Art/ViewModel/CSSArmsArmed.fbx";
        /// <summary>
        /// The file is exported already turned: grip_pose.py rotates the authored Z-up view space into
        /// Unity's by rewriting the vertices, because a rotation on the armature node is dropped when
        /// Unity re-parents the plain meshes. Nothing here may turn it a second time - doing exactly
        /// that laid the rifle back out along +Y, above the eye, where the viewmodel camera culls it.
        /// The parts therefore arrive standing forward along +Z, the axis BuildMuzzleMarker reads.
        ///
        /// The offset is the one framing knob. Measured off the built scene: the hands land at
        /// (0.036, -0.058, 0.324) and the muzzle at (0.10, -0.03, 0.86) in root space, so this pushes
        /// the rifle out of the centre line into the lower right - hands at viewport (0.62, 0.17),
        /// receiver (0.71, 0.32) - while the barrel still converges to (0.61, 0.40) beside the
        /// crosshair. Pulling it further right or lower takes the gloves out of the frame.
        /// </summary>
        private static readonly Vector3 ViewModelOffset = new Vector3(0.02f, -0.03f, 0.06f);

        private static readonly Dictionary<string, Material> Materials = new Dictionary<string, Material>();

        /// <summary>
        /// The two material families the arena is built from. Reflective ones have to sit next to
        /// matte ones or the difference is invisible, so the ground and walls deliberately take no
        /// reflection at all and the chrome and glass props are the only mirrors in frame.
        /// Nothing on the map emits: the sun is the only light source in the level allowed to glow,
        /// so every prop has to sell itself through albedo and reflection instead.
        /// </summary>
        private static readonly Dictionary<string, (float Metallic, float Smoothness, Color Emission)> Surfaces =
            new Dictionary<string, (float, float, Color)>
            {
                ["Ground"] = (0.00f, 0.12f, Color.black),
                ["Wall"] = (0.00f, 0.10f, Color.black),
                ["Cover"] = (0.02f, 0.16f, Color.black),
                ["Chrome"] = (1.00f, 0.96f, Color.black),
                ["Collectible"] = (0.20f, 0.94f, Color.black),
                ["Hazard"] = (0.05f, 0.25f, Color.black),
                ["PlayerAccent"] = (0.62f, 0.80f, Color.black),
            };

        // The glass panes are the only hand written shader left on the map: the Standard shader has no
        // way to be both transparent and probe-reflective.
        private static Material _glass;

        private static int _environmentLayer;
        private static int _playerLayer;
        private static int _viewModelLayer;

        [MenuItem("Game/Build Main Scene")]
        public static void BuildAll()
        {
            try
            {
                EnsureFolders();
                _environmentLayer = GetOrAddLayer("Environment");
                _playerLayer = GetOrAddLayer("Player");
                _viewModelLayer = GetOrAddLayer("ViewModel");

                // Rounded 9-slices first: every Image below points at one of these.
                UiSpriteFactory.Prepare();

                var config = EnsureConfig();

                // The controller is regenerated from the clips that exist, so a reimport stays reproducible.
                CharacterAnimatorBuilder.Build();

                var scene = NewScene();
                BuildEnvironment(config);
                var player = BuildPlayer(config);
                var rig = BuildCameraRig(player, config);
                var canvas = BuildCanvas();
                var screens = BuildScreens(canvas.transform);
                BuildRuntimeObjects(config, player, screens, rig);

                SaveAndRegisterScene(scene);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                Debug.Log("[SceneBuilder] SUCCESS: " + ScenePath);
            }
            catch (Exception e)
            {
                Debug.LogError("[SceneBuilder] FAILED: " + e);
                throw new BuildFailedException(e.Message);
            }
        }

        // ------------------------------------------------------------------ player build

        private const string PlayerOutput = "Build/Windows/NeonRelay.exe";

        /// <summary>
        /// Builds the standalone Windows player. Runs the scene builder first when the scene is
        /// missing so a single batchmode call produces a playable .exe.
        /// </summary>
        [MenuItem("Game/Build Windows Player")]
        public static void BuildWindowsPlayer()
        {
            if (!File.Exists(ScenePath)) BuildAll();

            var active = EditorUserBuildSettings.activeBuildTarget;
            if (active != BuildTarget.StandaloneWindows64)
                throw new BuildFailedException($"Active build target is {active}, expected StandaloneWindows64.");

            PlayerSettings.companyName = "Local";
            PlayerSettings.productName = "Neon Relay";
            // The lobby registry is plain HTTP on an address, so the player has to be told to allow it.
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = PlayerOutput,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            };

            BuildPipeline.BuildPlayer(options);

            // Unity 6 moved the build report types between namespaces; the artifact itself is the reliable check.
            var exe = Path.GetFullPath(PlayerOutput);
            Debug.Log($"[SceneBuilder] player build finished, exe={exe} exists={File.Exists(exe)}");
            if (!File.Exists(exe))
                throw new BuildFailedException($"Player build did not produce {exe}.");
        }

        // ------------------------------------------------------------------ scaffolding

        private static void EnsureFolders()
        {
            CreateFolder(SceneFolder);
            CreateFolder(MaterialFolder);
            CreateFolder(ShaderFolder);
            CreateFolder(DataFolder);
        }

        private static void CreateFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = path.Contains("/")
                ? path.Substring(0, path.LastIndexOf('/'))
                : "Assets";
            var name = Path.GetFileName(path);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static GameConfig EnsureConfig()
        {
            var config = AssetDatabase.LoadAssetAtPath<GameConfig>(ConfigPath);
            if (config == null)
            {
                config = ScriptableObject.CreateInstance<GameConfig>();
                AssetDatabase.CreateAsset(config, ConfigPath);
                Debug.Log("[SceneBuilder] created " + ConfigPath);
            }

            // Speeds are retuned to the pack's own animation cadence: walk lands exactly on the
            // 0.5 blend threshold and run on 1.0, which is what stops the feet from sliding.
            // Serialized fields of an existing asset keep their old values, so the builder applies them.
            config.walkSpeed = 3.0f;
            config.runSpeed = 6.0f;

            EditorUtility.SetDirty(config);
            return config;
        }

        private static Scene NewScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SceneManager.SetActiveScene(scene);
            return scene;
        }

        private static void SaveAndRegisterScene(Scene scene)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }

        private static GameObject NewGo(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        private static void SetRef(Component target, string property, UnityEngine.Object value)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(property);
            if (prop == null)
            {
                Debug.LogError($"[SceneBuilder] {target.GetType().Name} has no serialized field '{property}'.");
                return;
            }
            prop.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void SetLayerMask(Component target, string property, int layer)
        {
            if (layer < 0) return;
            var so = new SerializedObject(target);
            var prop = so.FindProperty(property);
            if (prop == null)
            {
                Debug.LogError($"[SceneBuilder] {target.GetType().Name} has no serialized field '{property}'.");
                return;
            }
            prop.intValue = 1 << layer;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------------ environment

        private static void BuildEnvironment(GameConfig config)
        {
            var world = NewGo("World");

            var lightGo = NewGo("Directional Light", world.transform);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(1f, 0.96f, 0.88f);
            light.intensity = 1.5f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(52f, -34f, 0f);
            ConfigureSkybox();
            ConfigureFog();
            BuildGlassMaterial();

            // A cold rim from the opposite side separates the player from the walls. No shadows:
            // two shadow-casting directionals would double the cascade cost for an effect that is
            // entirely about the silhouette.
            var rimGo = NewGo("Rim Light", world.transform);
            var rim = rimGo.AddComponent<Light>();
            rim.type = LightType.Directional;
            rim.color = new Color(0.42f, 0.72f, 1f);
            rim.intensity = 0.5f;
            rim.shadows = LightShadows.None;
            rimGo.transform.rotation = Quaternion.Euler(-24f, 148f, 0f);

            BuildReflectionProbe(world);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(world.transform, false);
            ground.transform.localScale = new Vector3(6f, 1f, 6f);
            ApplyMaterial(ground, "Ground", new Color(0.36f, 0.38f, 0.42f));
            SetLayerRecursive(ground, _environmentLayer);

            var walls = new (Vector3 pos, Vector3 scale, float yaw)[]
            {
                (new Vector3(0f, 2f, 30f), new Vector3(60f, 4f, 1f), 0f),
                (new Vector3(0f, 2f, -30f), new Vector3(60f, 4f, 1f), 0f),
                (new Vector3(30f, 2f, 0f), new Vector3(60f, 4f, 1f), 90f),
                (new Vector3(-30f, 2f, 0f), new Vector3(60f, 4f, 1f), 90f),
            };

            for (int i = 0; i < walls.Length; i++)
            {
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "Wall_" + i;
                wall.transform.SetParent(world.transform, false);
                wall.transform.localPosition = walls[i].pos;
                wall.transform.localScale = walls[i].scale;
                wall.transform.rotation = Quaternion.Euler(0f, walls[i].yaw, 0f);
                ApplyMaterial(wall, "Wall", new Color(0.46f, 0.49f, 0.56f));
                SetLayerRecursive(wall, _environmentLayer);
            }

            var cover = new Vector3[]
            {
                new Vector3(-8f, 0.9f, 6f), new Vector3(7f, 0.9f, -4f),
                new Vector3(14f, 0.9f, 9f), new Vector3(-15f, 0.9f, -11f),
                new Vector3(2f, 0.9f, 18f), new Vector3(-4f, 0.9f, -18f),
            };

            for (int i = 0; i < cover.Length; i++)
            {
                var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
                block.name = "Cover_" + i;
                block.transform.SetParent(world.transform, false);
                block.transform.localPosition = cover[i];
                block.transform.localScale = new Vector3(2.4f, 1.8f, 2.4f);
                SetLayerRecursive(block, _environmentLayer);

                // Three families, side by side: bare concrete that reflects nothing, polished chrome
                // that mirrors the whole arena, and glass that does both at once.
                if (i % 3 == 1) ApplyMaterial(block, "Chrome", new Color(0.86f, 0.88f, 0.92f));
                else ApplyMaterial(block, "Cover", new Color(0.52f, 0.46f, 0.36f));

                if (i % 3 == 2) AddGlassPane(world, block.transform);
            }

            var spawnGo = NewGo("SpawnPoint", world.transform);
            spawnGo.transform.localPosition = new Vector3(0f, 1.1f, 0f);
            spawnGo.AddComponent<RespawnAnchor>();

            BuildCollectiblesAndHazards(world, config);
        }

        private static void BuildCollectiblesAndHazards(GameObject world, GameConfig config)
        {
            var count = Mathf.Max(1, config.collectibleCount);
            for (int i = 0; i < count; i++)
            {
                var angle = i / (float)count * Mathf.PI * 2f;
                var radius = 11f + (i % 3) * 4f;

                var orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                orb.name = "Collectible_" + i;
                orb.transform.SetParent(world.transform, false);
                orb.transform.localPosition = new Vector3(
                    Mathf.Cos(angle) * radius, 1.4f, Mathf.Sin(angle) * radius);
                orb.transform.localScale = Vector3.one * 0.8f;
                ApplyMaterial(orb, "Collectible", new Color(0.14f, 0.58f, 0.74f));
                orb.GetComponent<Collider>().isTrigger = true;
                SetLayerRecursive(orb, _environmentLayer);
                orb.AddComponent<Collectible>();
            }

            var hazards = new Vector3[]
            {
                new Vector3(-6f, 0.25f, -8f), new Vector3(10f, 0.25f, 4f),
                new Vector3(-12f, 0.25f, 14f),
            };

            foreach (var position in hazards)
            {
                var hazard = GameObject.CreatePrimitive(PrimitiveType.Cube);
                hazard.name = "Hazard";
                hazard.transform.SetParent(world.transform, false);
                hazard.transform.localPosition = position;
                hazard.transform.localScale = new Vector3(5f, 0.5f, 5f);
                ApplyMaterial(hazard, "Hazard", new Color(0.62f, 0.16f, 0.13f));
                SetLayerRecursive(hazard, _environmentLayer);

                var volume = hazard.AddComponent<HazardVolume>();
                SetRef(volume, "config", config);
            }
        }

        private static GameObject BuildPlayer(GameConfig config)
        {
            var player = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            player.name = "Player";
            player.transform.position = new Vector3(0f, 1.1f, 0f);

            var capsule = player.GetComponent<CapsuleCollider>();
            if (capsule != null) UnityEngine.Object.DestroyImmediate(capsule);

            var controller = player.AddComponent<CharacterController>();
            controller.height = 1.8f;
            controller.radius = 0.35f;
            controller.center = new Vector3(0f, 0.9f, 0f);
            controller.stepOffset = 0.35f;
            controller.slopeLimit = 50f;

            var movement = player.AddComponent<FirstPersonController>();
            SetRef(movement, "config", config);
            SetLayerMask(movement, "groundedMask", _environmentLayer);

            var health = player.AddComponent<PlayerHealth>();
            SetRef(health, "config", config);

            if (!AttachCharacterModel(player, controller, movement, health))
            {
                // No imported model: keep the readable prototype instead of an invisible player.
                var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nose.name = "Facing";
                nose.transform.SetParent(player.transform, false);
                nose.transform.localPosition = new Vector3(0f, 0.25f, 0.34f);
                nose.transform.localScale = new Vector3(0.22f, 0.12f, 0.3f);
                var noseCollider = nose.GetComponent<Collider>();
                if (noseCollider != null) UnityEngine.Object.DestroyImmediate(noseCollider);
                ApplyMaterial(nose, "PlayerAccent", new Color(1f, 0.78f, 0.2f));
            }

            // First person: the collision capsule itself is never drawn, only the model (or fallback parts).
            var capsuleRenderer = player.GetComponent<Renderer>();
            if (capsuleRenderer != null) capsuleRenderer.enabled = false;

            SetLayerRecursive(player, _playerLayer);
            return player;
        }

        /// <summary>
        /// Puts the CC0 character model under the collision root, sized so its feet sit on the bottom of
        /// the CharacterController, and drives it with the generated Animator Controller.
        /// </summary>
        private static bool AttachCharacterModel(GameObject player, CharacterController controller,
            FirstPersonController movement, PlayerHealth health)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BodyPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[SceneBuilder] {BodyPath} not imported yet; falling back to primitives.");
                return false;
            }

            var body = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (body == null) return false;

            body.name = "Body";
            body.transform.SetParent(player.transform, false);
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = Quaternion.identity;

            var measured = BodyBounds(body);
            if (measured.size.y > 0.01f)
                body.transform.localScale = body.transform.localScale * (controller.height / measured.size.y);

            // Re-measure after scaling: the authored pivot of the pack is not guaranteed to be at the feet.
            measured = BodyBounds(body);
            var feetY = player.transform.position.y + controller.center.y - controller.height * 0.5f;
            body.transform.position += Vector3.up * (feetY - measured.min.y);

            var animator = body.GetComponent<Animator>();
            if (animator == null) animator = body.AddComponent<Animator>();
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            var runtimeController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AnimatorPath);
            if (runtimeController == null)
                Debug.LogWarning($"[SceneBuilder] {AnimatorPath} missing; the body will stay in its bind pose.");
            animator.runtimeAnimatorController = runtimeController;

            var driver = body.AddComponent<PlayerAnimatorDriver>();
            SetRef(driver, "movement", movement);
            SetRef(driver, "health", health);

            // The first person camera sits inside the head, so its own head is not drawn during a run.
            body.AddComponent<FirstPersonHeadHider>();

            var bound = BindCharacterTextures(body);
            var rifle = AttachWorldWeapon(body);

            Debug.Log($"[SceneBuilder] body={BodyPath} height={measured.size.y:F2}m " +
                      $"animator={(runtimeController == null ? "none" : runtimeController.name)} " +
                      $"texturesBound={bound} rifle={(rifle ? GunPath : "none")}");
            return true;
        }

        /// <summary>
        /// Puts the AK in the player's hand for the menu showcase and for anyone who sees the body.
        /// The pose is not guessed at: GunMount.fbx is the same mesh parented to the same hand bone
        /// in Blender, so its imported local transform is exactly what the hand needs.
        /// </summary>
        private static bool AttachWorldWeapon(GameObject body)
        {
            var gunPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GunPath);
            var mount = AssetDatabase.LoadAssetAtPath<GameObject>(GunMountPath);
            var hand = FindDeep(body.transform, HandBoneName);
            var authored = mount != null ? FindDeep(mount.transform, GunMeshName) : null;

            if (gunPrefab == null || hand == null || authored == null)
            {
                Debug.LogWarning($"[SceneBuilder] rifle skipped: prefab={gunPrefab != null} " +
                                 $"hand={hand != null} mountPose={authored != null}");
                return false;
            }

            var gun = PrefabUtility.InstantiatePrefab(gunPrefab) as GameObject;
            if (gun == null) return false;

            gun.name = "Rifle";
            gun.transform.SetParent(hand, false);
            gun.transform.localPosition = authored.localPosition;
            gun.transform.localRotation = authored.localRotation;
            gun.transform.localScale = authored.localScale;
            BindCharacterTextures(gun);

            Debug.Log($"[SceneBuilder] rifle parented to {hand.name} at " +
                      $"{authored.localPosition} rot={authored.localRotation.eulerAngles} " +
                      $"scale={authored.localScale}");
            return true;
        }

        /// <summary>Depth-first lookup by exact name, including nodes the importer leaves inactive.</summary>
        private static Transform FindDeep(Transform root, string name)
        {
            if (root.name == name) return root;
            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindDeep(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// The pack's FBX materials arrive with the right names but no textures bound (Quaternius ships the
        /// PNGs next to the FBX instead of embedding them), so each material slot is matched to
        /// Assets/Art/Textures/&lt;materialName&gt;.png and rebuilt as a real textured material.
        /// </summary>
        private static int BindCharacterTextures(GameObject body)
        {
            var shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Legacy Shaders/Diffuse");
            if (shader == null)
            {
                Debug.LogWarning("[SceneBuilder] no shader available to build character materials.");
                return 0;
            }

            var cache = new Dictionary<string, Material>();
            var boundCount = 0;

            foreach (var renderer in body.GetComponentsInChildren<Renderer>(true))
            {
                var slots = renderer.sharedMaterials;
                var changed = false;

                for (var i = 0; i < slots.Length; i++)
                {
                    var source = slots[i];
                    if (source == null) continue;
                    // A slot that already carries its own texture belongs to the model, not to the
                    // arena material set; rebuilding it would paste the Warrior's skin on another character.
                    if (source.mainTexture != null) continue;

                    var replacement = TexturedMaterial(source.name, shader, cache);
                    if (replacement == null) continue;

                    slots[i] = replacement;
                    changed = true;
                    boundCount++;
                }

                if (changed) renderer.sharedMaterials = slots;
            }

            return boundCount;
        }

        private static Material TexturedMaterial(string materialName, Shader shader, Dictionary<string, Material> cache)
        {
            if (cache.TryGetValue(materialName, out var cached)) return cached;

            var texture = FindTexture(materialName);
            if (texture == null)
            {
                Debug.LogWarning($"[SceneBuilder] no texture found for material '{materialName}'.");
                cache[materialName] = null;
                return null;
            }

            var path = $"{MaterialFolder}/{materialName}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.mainTexture = texture;
            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
            if (material.HasProperty("_Color")) material.color = Color.white;
            TuneSurface(material, SurfaceProfileFor(materialName));
            EditorUtility.SetDirty(material);

            cache[materialName] = material;
            return material;
        }

        private static Texture2D FindTexture(string materialName)
        {
            var guids = AssetDatabase.FindAssets($"{materialName} t:Texture2D", new[] { TextureFolder });
            if (guids == null || guids.Length == 0) return null;

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) != materialName) continue;
                return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            return null;
        }

        private static Bounds BodyBounds(GameObject body)
        {
            var renderers = body.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return new Bounds(body.transform.position, Vector3.zero);

            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        private static PlayerCameraRig BuildCameraRig(GameObject player, GameConfig config)
        {
            var rigGo = NewGo("CameraRig");
            var rig = rigGo.AddComponent<PlayerCameraRig>();

            var cameraGo = NewGo("Main Camera", rigGo.transform);
            cameraGo.tag = "MainCamera";
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = RenderSettings.skybox != null
                ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.09f, 0.11f, 0.16f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;
            camera.fieldOfView = config.menuFieldOfView;
            camera.allowMSAA = true;
            camera.allowHDR = true;
            // The viewmodel gets its own camera; drawing it here too would double it with the
            // wrong perspective.
            camera.cullingMask = ~(1 << _viewModelLayer);
            cameraGo.AddComponent<AudioListener>();

            SetRef(rig, "target", player.transform);
            SetRef(rig, "config", config);
            SetRef(rig, "cameraHolder", cameraGo.transform);

            var weapon = BuildWeapon(player, camera, config);
            var muzzle = BuildViewModel(cameraGo.transform, camera, rig, weapon);
            var effects = ConfigureShotFx(player, camera, weapon, muzzle);
            BuildArenaBridge(player, camera, weapon, effects);
            ConfigurePostFx(camera);
            return rig;
        }

        /// <summary>
        /// The rifle is fired from the eye camera rather than from the gun model, so a round leaves
        /// along exactly the vector the crosshair points at. The aim mask is the Environment layer on
        /// its own: the viewmodel sits centimetres in front of that camera, and a ray allowed to touch
        /// it would shoot the player's own rifle every time.
        /// </summary>
        private static HitscanWeapon BuildWeapon(GameObject player, Camera camera, GameConfig config)
        {
            var weapon = player.AddComponent<HitscanWeapon>();
            SetRef(weapon, "config", config);
            SetRef(weapon, "aimOrigin", camera.transform);
            SetLayerMask(weapon, "aimMask", _environmentLayer);

            return weapon;
        }

        /// <summary>
        /// The CS:S hands and their rifle, drawn by a depth-clearing camera parented to the eye
        /// transform so it inherits every head movement without any extra code.
        ///
        /// The camera is a sibling of the swaying viewmodel root, never its child: parented to the
        /// root it would swing along with the rifle and cancel the motion out on screen.
        /// </summary>
        private static Transform BuildViewModel(Transform holder, Camera mainCamera, PlayerCameraRig rig,
            HitscanWeapon weapon)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ViewModelPath);
            if (prefab == null)
            {
                Debug.LogWarning($"[SceneBuilder] {ViewModelPath} missing; first person keeps the body arms.");
                return null;
            }

            var root = NewGo("ViewModel", holder);
            root.transform.localPosition = ViewModelOffset;

            var cameraGo = NewGo("ViewModel Camera", holder);
            var camera = cameraGo.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.Depth;
            camera.cullingMask = 1 << _viewModelLayer;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = 8f;
            camera.fieldOfView = mainCamera.fieldOfView;
            camera.depth = mainCamera.depth + 10;
            camera.allowMSAA = true;
            camera.allowHDR = true;

            var hands = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (hands == null) return null;
            hands.name = "Hands";
            hands.transform.SetParent(root.transform, false);
            hands.transform.localPosition = Vector3.zero;
            hands.transform.localRotation = Quaternion.identity;
            SetLayerRecursive(hands, _viewModelLayer);
            var bound = BindCharacterTextures(hands);

            var driver = rig.gameObject.AddComponent<FirstPersonViewModel>();
            SetRef(driver, "viewModelRoot", root);
            SetRef(driver, "viewModelCamera", camera);
            SetRef(driver, "mainCamera", mainCamera);
            SetRef(driver, "weapon", weapon);

            var muzzle = BuildMuzzleMarker(root.transform, hands);

            Debug.Log($"[SceneBuilder] viewmodel={ViewModelPath} layer={_viewModelLayer} " +
                      $"offset={ViewModelOffset} texturesBound={bound}");
            return muzzle;
        }

        /// <summary>
        /// Where the barrel ends. Read off the imported mesh rather than typed in: the FBX comes in at
        /// a scale nobody reads out of the file, and a muzzle guessed in centimetres puts the flame
        /// either inside the rifle stock or a metre in front of the sight.
        ///
        /// The front-most renderer wins because the viewmodel is a single box otherwise: the hands
        /// reach around the furniture and only the barrel sticks out, so the union of the two would
        /// put the flame wherever the off-hand happened to be.
        /// </summary>
        private static Transform BuildMuzzleMarker(Transform root, GameObject hands)
        {
            Renderer front = null;
            Bounds frontBox = default;
            var best = float.NegativeInfinity;

            foreach (var renderer in hands.GetComponentsInChildren<Renderer>(true))
            {
                var min = root.InverseTransformPoint(renderer.bounds.min);
                var max = root.InverseTransformPoint(renderer.bounds.max);
                Debug.Log($"[SceneBuilder] viewmodel part '{renderer.name}' local z {min.z:F3}..{max.z:F3} " +
                          $"x {min.x:F3}..{max.x:F3} y {min.y:F3}..{max.y:F3}");
                if (max.z <= best) continue;
                best = max.z;
                front = renderer;
                frontBox = new Bounds((min + max) * 0.5f, max - min);
            }

            if (front == null || frontBox.size == Vector3.zero) return null;

            // The barrel runs along +z, so its own centre line and the face of its box is the tip.
            var tip = new Vector3(frontBox.center.x, frontBox.center.y,
                                  frontBox.max.z - 0.02f);

            var marker = NewGo("Muzzle", root);
            // The flame that gets parented here inherits this layer, and only the viewmodel camera
            // draws that one — on the default layer it would be occluded by the rifle it sits on.
            marker.layer = _viewModelLayer;
            marker.transform.localPosition = tip;
            Debug.Log($"[SceneBuilder] muzzle part='{front.name}' local={tip} box={frontBox.size}");
            return marker.transform;
        }

        /// <summary>
        /// The picture a round leaves behind. It hangs off the player rather than the rifle because the
        /// tracer is a world object that has to be seen by the eye camera, while only the flame rides
        /// the viewmodel; the muzzle transform is the single link between the two.
        /// </summary>
        private static ShotEffects ConfigureShotFx(GameObject player, Camera eye, HitscanWeapon weapon,
            Transform muzzle)
        {
            // The report lives at the scene root rather than on the shooter: a gunshot belongs to a
            // place in the arena, and a source parented to the player gets dragged along behind it.
            var sound = BuildShotAudio();

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShotFxShaderPath);
            if (shader == null)
            {
                Debug.LogWarning($"[SceneBuilder] {ShotFxShaderPath} missing; shots fire without a tracer.");
                return null;
            }

            var streak = ShotMaterial(ShotStreakMaterialPath, shader, 0f, true);
            var glow = ShotMaterial(ShotGlowMaterialPath, shader, 1f, false);

            var effects = player.AddComponent<ShotEffects>();
            SetRef(effects, "muzzle", muzzle);
            SetRef(effects, "aimOrigin", eye.transform);
            SetRef(effects, "streakMaterial", streak);
            SetRef(effects, "glowMaterial", glow);
            SetRef(effects, "sound", sound);
            SetRef(weapon, "effects", effects);
            return effects;
        }

        /// <summary>
        /// The online seam goes on the player rather than on a manager of its own: the pose it reports is
        /// the controller's, the tracers it replays are the rifle's, and the orbs it claims are found from
        /// the scene. Splitting that across objects would only make it reach for three references.
        /// </summary>
        private static void BuildArenaBridge(GameObject player, Camera eye, HitscanWeapon weapon,
            ShotEffects effects)
        {
            var bridge = player.AddComponent<Game.Online.ArenaBridge>();
            SetRef(bridge, "movement", player.GetComponent<FirstPersonController>());
            SetRef(bridge, "weapon", weapon);
            SetRef(bridge, "effects", effects);
            SetRef(bridge, "eye", eye.transform);
        }

        private static ShotAudio BuildShotAudio()
        {
            var audio = NewGo("Shot Audio").AddComponent<ShotAudio>();
            SetRef(audio, "clip", PrepareShotClip());
            return audio;
        }

        /// <summary>
        /// The shot sample, taken apart by the importer before it is ever played. Decompressed on load
        /// because a 0.6 s one-shot must not decode on the frame it is triggered. The recording is mono
        /// by itself, and stereo here comes from where the layers are placed, not from the file.
        /// </summary>
        private static AudioClip PrepareShotClip()
        {
            // A clip dropped into Assets between runs has no importer yet, and asking for one returns
            // null rather than the audio. Refresh once so the file is indexed before it is configured.
            if (AssetDatabase.LoadAssetAtPath<AudioClip>(ShotClipPath) == null) AssetDatabase.Refresh();

            var importer = AssetImporter.GetAtPath(ShotClipPath) as AudioImporter;
            if (importer != null)
            {
                var settings = importer.defaultSampleSettings;
                settings.loadType = AudioClipLoadType.DecompressOnLoad;
                settings.preloadAudioData = true;
                importer.defaultSampleSettings = settings;
                importer.SaveAndReimport();
            }

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(ShotClipPath);
            if (clip == null)
            {
                Debug.LogWarning($"[SceneBuilder] {ShotClipPath} missing; the rifle fires silent.");
                return null;
            }

            Debug.Log($"[SceneBuilder] shot clip={ShotClipPath} channels={clip.channels} " +
                      $"seconds={clip.length:F3} rate={clip.frequency}");
            return clip;
        }

        private static Material ShotMaterial(string path, Shader shader, float radial, bool vertexColor)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.SetFloat("_Radial", radial);
            material.SetFloat("_VertexColor", vertexColor ? 1f : 0f);
            material.SetColor("_Color", Color.white);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// Hands the world camera its colour grade. The shader is loaded as an asset and baked into a
        /// material asset on purpose: the stack looks the material up by reference, and a runtime
        /// Shader.Find would find nothing because the build strips shaders no shipped material uses.
        /// </summary>
        private static void ConfigurePostFx(Camera camera)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(PostFxShaderPath);
            if (shader == null) shader = Shader.Find("Hidden/NeonRelay/PostFX");
            if (shader == null)
            {
                Debug.LogWarning($"[SceneBuilder] {PostFxShaderPath} missing, arena renders ungraded.");
                return;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(PostFxMaterialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, PostFxMaterialPath);
            }

            material.shader = shader;
            EditorUtility.SetDirty(material);

            var stack = camera.gameObject.AddComponent<PostProcessStack>();
            SetRef(stack, "postFx", material);
            Debug.Log($"[SceneBuilder] post stack shader={shader.name} material={PostFxMaterialPath}");
        }

        // ------------------------------------------------------------------ canvas + screens

        private static GameObject BuildCanvas()
        {
            var canvasGo = NewGo("Canvas");
            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            var eventSystemGo = NewGo("EventSystem");
            var eventSystem = eventSystemGo.AddComponent<EventSystem>();
            eventSystem.sendNavigationEvents = true;
            eventSystemGo.AddComponent<InputSystemUIInputModule>();

            return canvasGo;
        }

        private sealed class ScreenBundle
        {
            public MainMenuScreen MainMenu;
            public HudScreen Hud;
            public PauseScreen Pause;
            public GameOverScreen GameOver;
            public SettingsScreen Settings;
            public LobbyListScreen LobbyList;
            public CreateLobbyScreen CreateLobby;
            public LobbyRoomScreen LobbyRoom;
        }

        private static ScreenBundle BuildScreens(Transform canvas)
        {
            return new ScreenBundle
            {
                MainMenu = BuildMainMenu(canvas),
                Hud = BuildHud(canvas),
                Pause = BuildPause(canvas),
                GameOver = BuildGameOver(canvas),
                Settings = BuildSettings(canvas),
                LobbyList = BuildLobbyList(canvas),
                CreateLobby = BuildCreateLobby(canvas),
                LobbyRoom = BuildLobbyRoom(canvas),
            };
        }

        // ------------------------------------------------------------------ ui style

        /// <summary>
        /// The whole interface draws from this table. It exists because the menu, HUD and lobby
        /// screens each grew up with their own hand-typed literals and ended up as eight slightly
        /// different greys; one named set is what makes them read as the same product.
        ///
        /// Surfaces step by elevation - scrim behind, panel raised, inset wells sunk, rows floated -
        /// and only four colours are allowed to carry meaning: primary blue, go green, danger red
        /// and the cyan accent used for readouts, never for buttons.
        /// </summary>
        private static class Ui
        {
            public static readonly Color Scrim = new Color(0.02f, 0.03f, 0.05f, 0.86f);
            public static readonly Color Panel = new Color(0.11f, 0.14f, 0.19f, 0.98f);
            public static readonly Color Inset = new Color(0.06f, 0.08f, 0.12f, 0.96f);
            public static readonly Color Row = new Color(0.17f, 0.21f, 0.27f, 0.94f);
            public static readonly Color Track = new Color(0.05f, 0.07f, 0.10f, 0.92f);
            public static readonly Color Bar = new Color(0.03f, 0.05f, 0.09f, 0.92f);

            public static readonly Color Neutral = new Color(0.24f, 0.29f, 0.37f, 0.98f);
            public static readonly Color Primary = new Color(0.20f, 0.45f, 0.85f, 0.98f);
            public static readonly Color Go = new Color(0.16f, 0.62f, 0.42f, 0.98f);
            public static readonly Color Danger = new Color(0.55f, 0.25f, 0.28f, 0.96f);
            public static readonly Color Accent = new Color(0.30f, 0.75f, 0.95f, 1f);

            public static readonly Color Text = new Color(0.95f, 0.97f, 1f);
            public static readonly Color Bright = new Color(0.93f, 0.96f, 1f);
            public static readonly Color Muted = new Color(0.68f, 0.76f, 0.85f);
            public static readonly Color Faint = new Color(0.48f, 0.55f, 0.64f);
            public static readonly Color Info = new Color(0.55f, 0.86f, 0.98f);

            public static readonly Color Health = new Color(0.28f, 0.82f, 0.45f, 1f);
            public static readonly Color Dash = new Color(0.95f, 0.78f, 0.25f, 1f);
            public static readonly Color Hit = new Color(1f, 0.86f, 0.35f, 1f);
        }

        private enum UiShape
        {
            /// <summary>No sprite: a full-bleed quad, for edge-to-edge bands and hairlines.</summary>
            None,
            /// <summary>Secondary control - buttons, fields, rows, bars.</summary>
            Chip,
            /// <summary>Raised card - dialogs and list wells.</summary>
            Panel,
            /// <summary>The one hero control on a screen.</summary>
            Pill,
        }

        /// <summary>Button height shared by every text button, so stacked menus read as one column.</summary>
        private const float ButtonHeight = 56f;

        private static Image Apply(Image image, UiShape shape)
        {
            var sprite = shape == UiShape.Panel ? UiSpriteFactory.PanelSprite
                : shape == UiShape.Pill ? UiSpriteFactory.PillSprite
                : shape == UiShape.Chip ? UiSpriteFactory.ChipSprite
                : null;

            // A missing generator output degrades to the old hard quad rather than an invisible UI.
            if (sprite == null) return image;
            image.sprite = sprite;
            image.type = Image.Type.Sliced;
            return image;
        }

        private static RectTransform Root(Transform parent, string name)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return rect;
        }

        private static CanvasGroup AddGroup(GameObject go)
        {
            var group = go.GetComponent<CanvasGroup>();
            if (group == null) group = go.AddComponent<CanvasGroup>();
            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;
            return group;
        }

        private static Text AddText(Transform parent, string name, string content, float size,
            Vector2 anchor, Vector2 pos, Vector2 dim,
            TextAnchor align = TextAnchor.MiddleLeft, Color? color = null)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = dim;

            var text = go.AddComponent<Text>();
            text.font = UiAssets.DefaultFont;
            text.fontSize = Mathf.RoundToInt(size);
            text.alignment = align;
            text.color = color ?? Ui.Text;
            text.text = content;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;
            return text;
        }

        private static Image AddPanel(Transform parent, string name, Color color,
            Vector2 anchor, Vector2 pos, Vector2 dim, UiShape shape = UiShape.Panel)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = dim;

            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = true;
            return Apply(image, shape);
        }

        private static Image AddStretchPanel(Transform parent, string name, Color color,
            UiShape shape = UiShape.None)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = go.AddComponent<Image>();
            image.color = color;
            return Apply(image, shape);
        }

        /// <summary>Full-width strip pinned to the top edge, whatever the window aspect ends up being.</summary>
        private static Image AddTopBarImage(Transform parent, string name, Color color, float height)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(0f, height);
            rect.anchoredPosition = Vector2.zero;

            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        private static Button AddButton(Transform parent, string name, string label,
            Vector2 anchor, Vector2 pos, Vector2 dim, Color? background = null, UiShape shape = UiShape.Chip)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = dim;

            var image = Apply(go.AddComponent<Image>(), shape);
            image.color = background ?? Ui.Primary;

            var button = go.AddComponent<Button>();
            StyleButton(button, image);

            var text = AddText(go.transform, "Label", label, 24f, Vector2.zero, Vector2.zero, Vector2.zero,
                TextAnchor.MiddleCenter, Ui.Text);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = Vector2.zero;
            text.rectTransform.offsetMax = Vector2.zero;

            return button;
        }

        /// <summary>
        /// Icon-only button used by the menu bar: coloured rounded background with the supplied glyph
        /// letterboxed inside, so the icon scales with the button instead of the button with the icon.
        /// </summary>
        private static Button AddIconButton(Transform parent, string name, string iconName,
            Vector2 anchor, Vector2 pos, Vector2 dim, Color background, float iconPadding,
            UiShape shape = UiShape.Chip)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = dim;

            var image = Apply(go.AddComponent<Image>(), shape);
            image.color = background;

            var button = go.AddComponent<Button>();
            StyleButton(button, image);

            var icon = NewGo("Icon", go.transform);
            var iconRect = icon.AddComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            var pad = dim.y * iconPadding;
            iconRect.offsetMin = Vector2.one * pad;
            iconRect.offsetMax = Vector2.one * -pad;

            var iconImage = icon.AddComponent<Image>();
            iconImage.sprite = LoadIcon(iconName);
            iconImage.color = Color.white;
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;

            return button;
        }

        /// <summary>
        /// One state table for every control. Sprite alpha multiplies the tint, so the hover lift can
        /// stay in colour terms and the rounded edge keeps its coverage; the fade is what stops the
        /// old instant pop that made the buttons feel like separate widgets.
        /// </summary>
        private static void StyleButton(Button button, Graphic target)
        {
            button.targetGraphic = target;
            var colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.14f, 1.16f, 1.2f, 1f);
            colors.pressedColor = new Color(0.72f, 0.74f, 0.8f, 1f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(1f, 1f, 1f, 0.34f);
            colors.fadeDuration = 0.08f;
            button.colors = colors;

            button.transition = Selectable.Transition.ColorTint;
        }

        /// <summary>
        /// The UI glyphs are shipped as PNGs, so each one is switched to Sprite import on the fly -
        /// otherwise Image.sprite has nothing to point at in a headless build.
        /// </summary>
        private static Sprite LoadIcon(string name)
        {
            var path = IconFolder + "/" + name + ".png";
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[SceneBuilder] icon {path} is missing; the button will show as a plain square.");
                return null;
            }

            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null)
            {
                // Freshly rendered PNGs sit on disk without a .meta until the database notices them.
                AssetDatabase.Refresh();
                importer = AssetImporter.GetAtPath(path) as TextureImporter;
            }

            if (importer != null && (importer.textureType != TextureImporterType.Sprite
                    || importer.spriteImportMode != SpriteImportMode.Single
                    || !importer.mipmapEnabled))
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.alphaIsTransparency = true;
                // The glyphs are authored at 256 and drawn at ~38, a 7:1 minification. Without the mip
                // chain bilinear picks four texels out of forty-five and the clean edge turns into the
                // staircase the user keeps pointing at.
                importer.mipmapEnabled = true;
                importer.filterMode = FilterMode.Bilinear;
                importer.SaveAndReimport();
            }

            return AssetDatabase.LoadAssetAtPath<Sprite>(path);
        }

        private static Slider AddSlider(Transform parent, string name, Vector2 anchor, Vector2 pos,
            Vector2 dim, float min, float max, float value)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = dim;

            AddStretchPanel(go.transform, "Background", Ui.Track, UiShape.Chip);

            var fillArea = NewGo("Fill Area", go.transform).AddComponent<RectTransform>();
            fillArea.anchorMin = new Vector2(0f, 0.22f);
            fillArea.anchorMax = new Vector2(1f, 0.78f);
            fillArea.offsetMin = new Vector2(4f, 0f);
            fillArea.offsetMax = new Vector2(-4f, 0f);

            var fill = AddStretchPanel(fillArea, "Fill", Ui.Accent, UiShape.Chip);

            var handleArea = NewGo("Handle Slide Area", go.transform).AddComponent<RectTransform>();
            handleArea.anchorMin = Vector2.zero;
            handleArea.anchorMax = Vector2.one;
            handleArea.offsetMin = new Vector2(9f, 0f);
            handleArea.offsetMax = new Vector2(-9f, 0f);

            var handle = NewGo("Handle", handleArea).AddComponent<RectTransform>();
            handle.anchorMin = new Vector2(0f, 0.5f);
            handle.anchorMax = new Vector2(0f, 0.5f);
            handle.sizeDelta = new Vector2(18f, 26f);
            var handleImage = Apply(handle.gameObject.AddComponent<Image>(), UiShape.Chip);
            handleImage.color = Color.white;

            var slider = go.AddComponent<Slider>();
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle;
            slider.targetGraphic = handleImage;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = value;
            return slider;
        }

        private static Toggle AddToggle(Transform parent, string name, string label,
            Vector2 anchor, Vector2 pos)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = new Vector2(320f, 32f);

            var check = AddPanel(go.transform, "Checkmark", Ui.Accent,
                new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(26f, 26f), UiShape.Chip);

            var toggle = go.AddComponent<Toggle>();
            toggle.isOn = false;
            toggle.targetGraphic = check;
            toggle.graphic = check;

            AddText(go.transform, "Label", label, 22f, new Vector2(0f, 0.5f),
                new Vector2(190f, 0f), new Vector2(300f, 28f));
            return toggle;
        }

        /// <summary>Track whose child fill is resized at runtime through anchorMax.x.</summary>
        private static RectTransform AddBar(Transform parent, string name, Vector2 anchor, Vector2 pos,
            Vector2 dim, Color fillColor)
        {
            var track = AddPanel(parent, name, Ui.Track, anchor, pos, dim, UiShape.Chip);
            track.rectTransform.pivot = new Vector2(0f, 0.5f);

            var fillGo = NewGo("Fill", track.transform);
            var fillRect = fillGo.AddComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(1f, 1f);
            fillRect.pivot = new Vector2(0f, 0.5f);
            fillRect.offsetMin = new Vector2(3f, 3f);
            fillRect.offsetMax = new Vector2(-3f, -3f);

            var fill = Apply(fillGo.AddComponent<Image>(), UiShape.Chip);
            fill.color = fillColor;
            fill.raycastTarget = false;
            return fillRect;
        }

        /// <summary>
        /// A crosshair bar: pinned to the canvas centre in pixels, not stretched, because HudScreen
        /// walks the four ticks outward by the weapon's current spread. Centre anchoring is what makes
        /// that distance mean the same angle on any window size.
        /// </summary>
        private static RectTransform AddCrosshairTick(Transform parent, string name, Vector2 size,
            Vector2 pos, Color color)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = pos;

            var image = go.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return rect;
        }

        /// <summary>
        /// The hit confirmation X. Four straight ticks with the group rotated 45 degrees, so no diagonal
        /// artwork is needed and it lines up with the crosshair gap it replaces for a moment.
        /// </summary>
        private static CanvasGroup AddHitMarker(Transform parent, Color color)
        {
            var go = NewGo("HitMarker", parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;
            rect.localEulerAngles = new Vector3(0f, 0f, 45f);

            var group = go.AddComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            const float length = 12f;
            const float reach = 16f;
            AddCrosshairTick(go.transform, "Up", new Vector2(2f, length),
                new Vector2(0f, reach - length * 0.5f), color);
            AddCrosshairTick(go.transform, "Down", new Vector2(2f, length),
                new Vector2(0f, -(reach - length * 0.5f)), color);
            AddCrosshairTick(go.transform, "Right", new Vector2(length, 2f),
                new Vector2(reach - length * 0.5f, 0f), color);
            AddCrosshairTick(go.transform, "Left", new Vector2(length, 2f),
                new Vector2(-(reach - length * 0.5f), 0f), color);

            return group;
        }

        private static MainMenuScreen BuildMainMenu(Transform canvas)
        {
            var root = Root(canvas, "MainMenu").gameObject;
            var group = AddGroup(root);
            // Light tint instead of a wall: the character standing in the arena is the menu backdrop.
            // Kept under a quarter opacity — anything heavier and the dusk sky behind it reads as black.
            AddStretchPanel(root.transform, "Backdrop", new Color(0.03f, 0.05f, 0.09f, 0.22f));

            // Icon-only bar: settings on the left, play in the middle, quit on the right.
            // All three are 64 units tall and share the pill radius, so the strip reads as one control
            // row; PLAY differs only in width and colour, which is what marks it as the action.
            var bar = AddTopBarImage(root.transform, "TopBar", Ui.Bar, 104f);

            var settings = AddIconButton(bar.transform, "SettingsButton", "IconSettings",
                new Vector2(0f, 0.5f), new Vector2(56f, 0f), new Vector2(64f, 64f),
                Ui.Neutral, 0.20f, UiShape.Pill);
            var play = AddIconButton(bar.transform, "PlayButton", "IconPlay",
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(112f, 64f),
                Ui.Go, 0.26f, UiShape.Pill);
            var quit = AddIconButton(bar.transform, "QuitButton", "IconQuit",
                new Vector2(1f, 0.5f), new Vector2(-56f, 0f), new Vector2(64f, 64f),
                Ui.Danger, 0.20f, UiShape.Pill);

            AddText(root.transform, "Title", "ORBS", 76f, new Vector2(0.5f, 0.80f), Vector2.zero,
                new Vector2(900f, 110f), TextAnchor.MiddleCenter, Ui.Bright);
            AddText(root.transform, "Hint",
                "move with WASD, Shift to run, Space to jump, left click to fire", 22f,
                new Vector2(0.5f, 0.72f), Vector2.zero, new Vector2(900f, 34f), TextAnchor.MiddleCenter,
                Ui.Muted);

            var version = AddText(root.transform, "Version", "-", 18f, new Vector2(0.5f, 0.008f), Vector2.zero,
                new Vector2(600f, 24f), TextAnchor.MiddleCenter, Ui.Faint);

            var screen = root.AddComponent<MainMenuScreen>();
            SetRef(screen, "playButton", play);
            SetRef(screen, "settingsButton", settings);
            SetRef(screen, "quitButton", quit);
            SetRef(screen, "versionLabel", version);
            SetRef(screen, "group", group);
            return screen;
        }

        private static HudScreen BuildHud(Transform canvas)
        {
            var root = Root(canvas, "Hud").gameObject;
            var group = AddGroup(root);

            var healthFill = AddBar(root.transform, "HealthTrack", new Vector2(0f, 1f), new Vector2(36f, -76f),
                new Vector2(340f, 26f), Ui.Health);
            var dashFill = AddBar(root.transform, "DashTrack", new Vector2(0f, 1f), new Vector2(36f, -108f),
                new Vector2(220f, 14f), Ui.Dash);

            var score = AddText(root.transform, "Score", "Score  0", 34f, new Vector2(1f, 1f),
                new Vector2(-190f, -52f), new Vector2(340f, 44f), TextAnchor.MiddleRight, Ui.Bright);
            var orbs = AddText(root.transform, "Orbs", "Orbs  0/0", 26f, new Vector2(1f, 1f),
                new Vector2(-190f, -96f), new Vector2(340f, 34f), TextAnchor.MiddleRight, Ui.Info);
            var timer = AddText(root.transform, "Timer", "00:00.0", 26f, new Vector2(1f, 1f),
                new Vector2(-190f, -132f), new Vector2(340f, 34f), TextAnchor.MiddleRight, Ui.Muted);
            var hint = AddText(root.transform, "ControlsHint",
                "WASD move   Shift run   Ctrl dash   Space jump   LMB fire   R reload   Esc pause", 20f,
                new Vector2(0.5f, 0f), new Vector2(0f, 42f), new Vector2(1100f, 30f),
                TextAnchor.MiddleCenter, Ui.Faint);

            var portrait = AddPanel(root.transform, "Portrait", Color.white, new Vector2(0f, 1f),
                new Vector2(26f, -26f), new Vector2(34f, 34f), UiShape.Chip);

            // Aim feedback sits dead centre and the magazine counter under it, so both are read without
            // moving the eye off the target.
            var tickColor = new Color(0.92f, 0.95f, 1f, 0.9f);
            AddCrosshairTick(root.transform, "CrosshairDot", new Vector2(3f, 3f), Vector2.zero, tickColor);
            var crosshairUp = AddCrosshairTick(root.transform, "CrosshairUp",
                new Vector2(2f, 14f), new Vector2(0f, 12f), tickColor);
            var crosshairDown = AddCrosshairTick(root.transform, "CrosshairDown",
                new Vector2(2f, 14f), new Vector2(0f, -12f), tickColor);
            var crosshairRight = AddCrosshairTick(root.transform, "CrosshairRight",
                new Vector2(14f, 2f), new Vector2(12f, 0f), tickColor);
            var crosshairLeft = AddCrosshairTick(root.transform, "CrosshairLeft",
                new Vector2(14f, 2f), new Vector2(-12f, 0f), tickColor);
            var hitMarker = AddHitMarker(root.transform, Ui.Hit);

            var ammo = AddText(root.transform, "Ammo", "-", 30f, new Vector2(1f, 0f),
                new Vector2(-44f, 54f), new Vector2(260f, 40f), TextAnchor.MiddleRight, Ui.Bright);

            var pauseButton = AddButton(root.transform, "PauseButton", "II", new Vector2(0.5f, 1f),
                new Vector2(0f, -50f), new Vector2(64f, 44f), Ui.Neutral);

            var screen = root.AddComponent<HudScreen>();
            SetRef(screen, "healthFill", healthFill);
            SetRef(screen, "dashFill", dashFill);
            SetRef(screen, "scoreText", score);
            SetRef(screen, "collectibleText", orbs);
            SetRef(screen, "timerText", timer);
            SetRef(screen, "hintText", hint);
            SetRef(screen, "pauseButton", pauseButton);
            SetRef(screen, "portrait", portrait);
            SetRef(screen, "crosshairUp", crosshairUp);
            SetRef(screen, "crosshairDown", crosshairDown);
            SetRef(screen, "crosshairLeft", crosshairLeft);
            SetRef(screen, "crosshairRight", crosshairRight);
            SetRef(screen, "hitMarker", hitMarker);
            SetRef(screen, "ammoText", ammo);
            SetRef(screen, "group", group);
            return screen;
        }

        private static PauseScreen BuildPause(Transform canvas)
        {
            var root = Root(canvas, "PauseMenu").gameObject;
            var group = AddGroup(root);
            AddStretchPanel(root.transform, "Backdrop", Ui.Scrim);

            AddText(root.transform, "Title", "PAUSED", 72f, new Vector2(0.5f, 0.66f), Vector2.zero,
                new Vector2(700f, 100f), TextAnchor.MiddleCenter);

            var resume = AddButton(root.transform, "ResumeButton", "RESUME", new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(340f, ButtonHeight));
            var settings = AddButton(root.transform, "SettingsButton", "SETTINGS", new Vector2(0.5f, 0.42f),
                Vector2.zero, new Vector2(340f, ButtonHeight), Ui.Neutral);
            var menu = AddButton(root.transform, "MenuButton", "MAIN MENU", new Vector2(0.5f, 0.34f),
                Vector2.zero, new Vector2(340f, ButtonHeight), Ui.Danger);

            var screen = root.AddComponent<PauseScreen>();
            SetRef(screen, "resumeButton", resume);
            SetRef(screen, "settingsButton", settings);
            SetRef(screen, "menuButton", menu);
            SetRef(screen, "group", group);
            return screen;
        }

        private static GameOverScreen BuildGameOver(Transform canvas)
        {
            var root = Root(canvas, "GameOver").gameObject;
            var group = AddGroup(root);
            AddStretchPanel(root.transform, "Backdrop", Ui.Scrim);

            var title = AddText(root.transform, "ResultTitle", "Run over", 88f, new Vector2(0.5f, 0.7f),
                Vector2.zero, new Vector2(900f, 120f), TextAnchor.MiddleCenter);
            var score = AddText(root.transform, "ResultScore", "Score  0", 34f, new Vector2(0.5f, 0.56f),
                Vector2.zero, new Vector2(600f, 48f), TextAnchor.MiddleCenter, Ui.Bright);
            var time = AddText(root.transform, "ResultTime", "Time  00:00", 28f, new Vector2(0.5f, 0.5f),
                Vector2.zero, new Vector2(600f, 40f), TextAnchor.MiddleCenter, Ui.Muted);

            var retry = AddButton(root.transform, "RetryButton", "RETRY", new Vector2(0.5f, 0.38f),
                Vector2.zero, new Vector2(340f, ButtonHeight), Ui.Go);
            var menu = AddButton(root.transform, "MenuButton", "MAIN MENU", new Vector2(0.5f, 0.3f),
                Vector2.zero, new Vector2(340f, ButtonHeight), Ui.Neutral);

            var screen = root.AddComponent<GameOverScreen>();
            SetRef(screen, "resultTitle", title);
            SetRef(screen, "resultScore", score);
            SetRef(screen, "resultTime", time);
            SetRef(screen, "retryButton", retry);
            SetRef(screen, "menuButton", menu);
            SetRef(screen, "group", group);
            return screen;
        }

        private static SettingsScreen BuildSettings(Transform canvas)
        {
            var root = Root(canvas, "Settings").gameObject;
            var group = AddGroup(root);
            AddStretchPanel(root.transform, "Backdrop", Ui.Scrim);

            var panel = AddPanel(root.transform, "Panel", Ui.Panel,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820f, 520f));

            AddText(panel.transform, "Title", "SETTINGS", 46f, new Vector2(0.5f, 1f), new Vector2(0f, -56f),
                new Vector2(600f, 60f), TextAnchor.MiddleCenter);

            AddText(panel.transform, "VolumeLabel", "Master volume", 24f, new Vector2(0f, 0.78f),
                new Vector2(190f, 0f), new Vector2(300f, 32f));
            var volume = AddSlider(panel.transform, "VolumeSlider", new Vector2(0f, 0.78f),
                new Vector2(560f, 0f), new Vector2(420f, 26f),
                SettingsService.MinVolume, SettingsService.MaxVolume, 0.8f);
            var volumeValue = AddText(panel.transform, "VolumeValue", "80%", 22f, new Vector2(1f, 0.78f),
                new Vector2(-24f, 0f), new Vector2(140f, 30f), TextAnchor.MiddleRight, Ui.Info);

            AddText(panel.transform, "SensitivityLabel", "Look sensitivity", 24f, new Vector2(0f, 0.64f),
                new Vector2(190f, 0f), new Vector2(300f, 32f));
            var sensitivity = AddSlider(panel.transform, "SensitivitySlider", new Vector2(0f, 0.64f),
                new Vector2(560f, 0f), new Vector2(420f, 26f),
                SettingsService.MinSensitivity, SettingsService.MaxSensitivity, 1f);
            var sensitivityValue = AddText(panel.transform, "SensitivityValue", "1.00", 22f,
                new Vector2(1f, 0.64f), new Vector2(-24f, 0f), new Vector2(140f, 30f),
                TextAnchor.MiddleRight, Ui.Info);

            var invert = AddToggle(panel.transform, "InvertYToggle", "Invert Y axis",
                new Vector2(0f, 0.48f), new Vector2(220f, 0f));
            var fullscreen = AddToggle(panel.transform, "FullscreenToggle", "Fullscreen",
                new Vector2(0f, 0.38f), new Vector2(220f, 0f));

            var close = AddButton(panel.transform, "CloseButton", "BACK", new Vector2(0.34f, 0.12f),
                Vector2.zero, new Vector2(240f, ButtonHeight), Ui.Neutral);
            var reset = AddButton(panel.transform, "ResetButton", "RESET", new Vector2(0.66f, 0.12f),
                Vector2.zero, new Vector2(240f, ButtonHeight), Ui.Primary);

            var screen = root.AddComponent<SettingsScreen>();
            SetRef(screen, "volumeSlider", volume);
            SetRef(screen, "sensitivitySlider", sensitivity);
            SetRef(screen, "invertYToggle", invert);
            SetRef(screen, "fullscreenToggle", fullscreen);
            SetRef(screen, "closeButton", close);
            SetRef(screen, "resetButton", reset);
            SetRef(screen, "volumeValue", volumeValue);
            SetRef(screen, "sensitivityValue", sensitivityValue);
            SetRef(screen, "group", group);
            return screen;
        }

        // ------------------------------------------------------------------ lobby screens

        /// <summary>
        /// Single-line text box. uGUI's InputField owns the two child texts it writes into, so both
        /// are stretched inside the frame and only the field itself is a raycast target.
        /// </summary>
        private static InputField AddInputField(Transform parent, string name, string placeholder,
            Vector2 anchor, Vector2 pos, Vector2 dim)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = dim;

            var image = Apply(go.AddComponent<Image>(), UiShape.Chip);
            image.color = Ui.Inset;
            image.raycastTarget = true;

            var inset = new Vector2(14f, 6f);
            var text = AddText(go.transform, "Text", string.Empty, 24f, Vector2.zero, Vector2.zero,
                Vector2.zero, TextAnchor.MiddleLeft, Ui.Text);
            text.rectTransform.anchorMin = Vector2.zero;
            text.rectTransform.anchorMax = Vector2.one;
            text.rectTransform.offsetMin = inset;
            text.rectTransform.offsetMax = -inset;

            var hint = AddText(go.transform, "Placeholder", placeholder, 24f, Vector2.zero, Vector2.zero,
                Vector2.zero, TextAnchor.MiddleLeft, Ui.Faint);
            hint.rectTransform.anchorMin = Vector2.zero;
            hint.rectTransform.anchorMax = Vector2.one;
            hint.rectTransform.offsetMin = inset;
            hint.rectTransform.offsetMax = -inset;

            var field = go.AddComponent<InputField>();
            field.textComponent = text;
            field.placeholder = hint;
            field.characterLimit = 24;
            field.contentType = InputField.ContentType.Standard;
            field.lineType = InputField.LineType.SingleLine;
            return field;
        }

        /// <summary>Column that lays out lobby rows for itself; rows are clones of an inactive template.</summary>
        private static RectTransform AddRowList(Transform parent, string name, Vector2 anchor, Vector2 pos,
            Vector2 dim)
        {
            var go = NewGo(name, parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = dim;

            var background = Apply(go.AddComponent<Image>(), UiShape.Panel);
            background.color = Ui.Inset;
            background.raycastTarget = false;

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 10f;
            layout.padding = new RectOffset(14, 14, 14, 14);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            return rect;
        }

        private static GameObject AddLobbyRowTemplate(RectTransform list)
        {
            var go = NewGo("RowTemplate", list);
            go.AddComponent<RectTransform>();
            var sizing = go.AddComponent<LayoutElement>();
            sizing.preferredHeight = 64f;

            var background = Apply(go.AddComponent<Image>(), UiShape.Chip);
            background.color = Ui.Row;
            background.raycastTarget = false;

            var name = AddText(go.transform, "Name", "Lobby", 26f, new Vector2(0f, 0.5f), new Vector2(228f, 0f),
                new Vector2(420f, 40f), TextAnchor.MiddleLeft, Ui.Text);
            var occupancy = AddText(go.transform, "Players", "0 / 4", 24f, new Vector2(0.58f, 0.5f), Vector2.zero,
                new Vector2(140f, 40f), TextAnchor.MiddleCenter, Ui.Info);
            var host = AddText(go.transform, "Host", "-", 22f, new Vector2(0.73f, 0.5f), Vector2.zero,
                new Vector2(220f, 40f), TextAnchor.MiddleCenter, Ui.Muted);
            var join = AddButton(go.transform, "JoinButton", "JOIN", new Vector2(1f, 0.5f), new Vector2(-70f, 0f),
                new Vector2(120f, 44f), Ui.Primary);

            var row = go.AddComponent<LobbyRow>();
            SetRef(row, "lobbyName", name);
            SetRef(row, "occupancy", occupancy);
            SetRef(row, "hostName", host);
            SetRef(row, "joinButton", join);

            go.SetActive(false);
            return go;
        }

        private static GameObject AddPlayerRowTemplate(RectTransform list)
        {
            var go = NewGo("RowTemplate", list);
            go.AddComponent<RectTransform>();
            var sizing = go.AddComponent<LayoutElement>();
            sizing.preferredHeight = 54f;

            var background = Apply(go.AddComponent<Image>(), UiShape.Chip);
            background.color = Ui.Row;
            background.raycastTarget = false;

            var name = AddText(go.transform, "Name", "Player", 26f, new Vector2(0f, 0.5f), new Vector2(278f, 0f),
                new Vector2(520f, 40f), TextAnchor.MiddleLeft, Ui.Text);
            var tag = AddText(go.transform, "Tag", string.Empty, 22f, new Vector2(1f, 0.5f), new Vector2(-104f, 0f),
                new Vector2(160f, 40f), TextAnchor.MiddleRight, Ui.Health);

            var row = go.AddComponent<LobbyPlayerRow>();
            SetRef(row, "playerName", name);
            SetRef(row, "tag", tag);

            go.SetActive(false);
            return go;
        }

        /// <summary>
        /// The corner every lobby screen carries: the name other players see, editable in place.
        /// It finds the service itself because it is a child widget, not a screen.
        /// </summary>
        private static void AddNicknamePanel(Transform parent)
        {
            var go = NewGo("Nickname", parent);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-40f, 34f);
            rect.sizeDelta = new Vector2(380f, 92f);

            var card = Apply(go.AddComponent<Image>(), UiShape.Panel);
            card.color = Ui.Panel;
            card.raycastTarget = false;

            var field = AddInputField(go.transform, "Field", "nickname", new Vector2(0.5f, 0.28f),
                Vector2.zero, new Vector2(340f, 46f));
            var caption = AddText(go.transform, "Caption", "You", 20f, new Vector2(1f, 1f),
                new Vector2(-16f, -12f), new Vector2(380f, 26f), TextAnchor.MiddleRight,
                Ui.Muted);

            var panel = go.AddComponent<NicknamePanel>();
            SetRef(panel, "field", field);
            SetRef(panel, "caption", caption);
        }

        private static LobbyListScreen BuildLobbyList(Transform canvas)
        {
            var root = Root(canvas, "LobbyBrowser").gameObject;
            var group = AddGroup(root);
            AddStretchPanel(root.transform, "Backdrop", Ui.Scrim);

            var panel = AddPanel(root.transform, "Panel", Ui.Panel,
                new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(1040f, 780f));

            AddText(panel.transform, "Title", "ONLINE LOBBIES", 42f, new Vector2(0.5f, 1f), new Vector2(0f, -48f),
                new Vector2(760f, 52f), TextAnchor.MiddleCenter, Ui.Bright);
            var status = AddText(panel.transform, "Status", "Connecting...", 22f, new Vector2(0.5f, 1f),
                new Vector2(0f, -94f), new Vector2(940f, 28f), TextAnchor.MiddleCenter, Ui.Muted);

            var list = AddRowList(panel.transform, "Rows", new Vector2(0.5f, 0.5f), new Vector2(0f, 35f),
                new Vector2(980f, 460f));
            var template = AddLobbyRowTemplate(list);

            var create = AddButton(panel.transform, "CreateButton", "CREATE LOBBY", new Vector2(0.30f, 0.065f),
                Vector2.zero, new Vector2(280f, ButtonHeight), Ui.Go);
            var refresh = AddButton(panel.transform, "RefreshButton", "REFRESH", new Vector2(0.58f, 0.065f),
                Vector2.zero, new Vector2(190f, ButtonHeight), Ui.Primary);
            var back = AddButton(panel.transform, "BackButton", "BACK", new Vector2(0.79f, 0.065f),
                Vector2.zero, new Vector2(170f, ButtonHeight), Ui.Neutral);

            AddNicknamePanel(root.transform);

            var screen = root.AddComponent<LobbyListScreen>();
            SetRef(screen, "rowRoot", list);
            SetRef(screen, "rowTemplate", template);
            SetRef(screen, "statusText", status);
            SetRef(screen, "createButton", create);
            SetRef(screen, "refreshButton", refresh);
            SetRef(screen, "backButton", back);
            SetRef(screen, "group", group);
            return screen;
        }

        private static CreateLobbyScreen BuildCreateLobby(Transform canvas)
        {
            var root = Root(canvas, "CreateLobby").gameObject;
            var group = AddGroup(root);
            AddStretchPanel(root.transform, "Scrim", Ui.Scrim);

            var panel = AddPanel(root.transform, "Panel", Ui.Panel,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(780f, 380f));

            AddText(panel.transform, "Title", "CREATE LOBBY", 38f, new Vector2(0.5f, 1f), new Vector2(0f, -44f),
                new Vector2(620f, 46f), TextAnchor.MiddleCenter, Ui.Bright);
            AddText(panel.transform, "NameLabel", "Lobby name", 22f, new Vector2(0.5f, 0.68f),
                new Vector2(0f, -18f), new Vector2(580f, 28f), TextAnchor.MiddleLeft, Ui.Muted);
            var field = AddInputField(panel.transform, "NameField", "for example Red Arena",
                new Vector2(0.5f, 0.52f), Vector2.zero, new Vector2(580f, 56f));
            var status = AddText(panel.transform, "Status", "-", 20f, new Vector2(0.5f, 0.36f), Vector2.zero,
                new Vector2(640f, 28f), TextAnchor.MiddleCenter, Ui.Muted);

            var create = AddButton(panel.transform, "CreateButton", "CREATE", new Vector2(0.34f, 0.14f),
                Vector2.zero, new Vector2(240f, ButtonHeight), Ui.Go);
            var cancel = AddButton(panel.transform, "CancelButton", "CANCEL", new Vector2(0.67f, 0.14f),
                Vector2.zero, new Vector2(240f, ButtonHeight), Ui.Neutral);

            var screen = root.AddComponent<CreateLobbyScreen>();
            SetRef(screen, "lobbyNameField", field);
            SetRef(screen, "statusText", status);
            SetRef(screen, "createButton", create);
            SetRef(screen, "cancelButton", cancel);
            SetRef(screen, "group", group);
            return screen;
        }

        private static LobbyRoomScreen BuildLobbyRoom(Transform canvas)
        {
            var root = Root(canvas, "LobbyRoom").gameObject;
            var group = AddGroup(root);
            AddStretchPanel(root.transform, "Backdrop", Ui.Scrim);

            var panel = AddPanel(root.transform, "Panel", Ui.Panel,
                new Vector2(0.5f, 0.5f), new Vector2(0f, 30f), new Vector2(900f, 700f));

            var title = AddText(panel.transform, "Title", "LOBBY", 40f, new Vector2(0.5f, 1f), new Vector2(0f, -46f),
                new Vector2(760f, 50f), TextAnchor.MiddleCenter, Ui.Bright);
            var status = AddText(panel.transform, "Status", "Refreshing...", 22f, new Vector2(0.5f, 1f),
                new Vector2(0f, -92f), new Vector2(820f, 28f), TextAnchor.MiddleCenter, Ui.Muted);

            AddText(panel.transform, "RosterLabel", "PLAYERS", 22f, new Vector2(0f, 1f), new Vector2(134f, -128f),
                new Vector2(200f, 28f), TextAnchor.MiddleLeft, Ui.Faint);
            var list = AddRowList(panel.transform, "Rows", new Vector2(0.5f, 0.5f), new Vector2(0f, 20f),
                new Vector2(820f, 360f));
            var template = AddPlayerRowTemplate(list);

            var start = AddButton(panel.transform, "StartButton", "START MATCH", new Vector2(0.5f, 0.075f),
                Vector2.zero, new Vector2(320f, ButtonHeight), Ui.Go);
            var leave = AddButton(panel.transform, "LeaveButton", "LEAVE", new Vector2(0.84f, 0.075f),
                Vector2.zero, new Vector2(180f, ButtonHeight), Ui.Danger);

            AddNicknamePanel(root.transform);

            var screen = root.AddComponent<LobbyRoomScreen>();
            SetRef(screen, "rowRoot", list);
            SetRef(screen, "rowTemplate", template);
            SetRef(screen, "lobbyTitle", title);
            SetRef(screen, "statusText", status);
            SetRef(screen, "startButton", start);
            SetRef(screen, "leaveButton", leave);
            SetRef(screen, "group", group);
            return screen;
        }

        // ------------------------------------------------------------------ runtime wiring

        private static void BuildRuntimeObjects(GameConfig config, GameObject player, ScreenBundle screens,
            PlayerCameraRig rig)
        {
            var world = GameObject.Find("World");
            var health = player.GetComponent<PlayerHealth>();
            var movement = player.GetComponent<FirstPersonController>();

            SetRef(movement, "cameraRig", rig);

            var collectibles = world.GetComponentsInChildren<Collectible>(true);
            var hazards = world.GetComponentsInChildren<HazardVolume>(true);

            var runGo = NewGo("RunController");
            var run = runGo.AddComponent<RunController>();
            SetRef(run, "config", config);
            SetRef(run, "player", player.transform);
            SetRef(run, "playerHealth", health);
            SetRef(run, "spawnPoint", world.GetComponentInChildren<RespawnAnchor>(true));
            SetArray(run, "collectibles", collectibles);
            SetArray(run, "hazards", hazards);

            runGo.AddComponent<GameFlowDriver>();

            var binderGo = NewGo("UiBinder");
            var binder = binderGo.AddComponent<UiSceneBinder>();
            SetRef(binder, "mainMenuScreen", screens.MainMenu);
            SetRef(binder, "hudScreen", screens.Hud);
            SetRef(binder, "pauseScreen", screens.Pause);
            SetRef(binder, "gameOverScreen", screens.GameOver);
            SetRef(binder, "settingsScreen", screens.Settings);
            SetRef(binder, "lobbyListScreen", screens.LobbyList);
            SetRef(binder, "createLobbyScreen", screens.CreateLobby);
            SetRef(binder, "lobbyRoomScreen", screens.LobbyRoom);
            SetRef(binder, "config", config);
            SetRef(binder, "playerHealth", health);
            SetRef(binder, "playerController", movement);
            SetRef(binder, "weapon", player.GetComponent<HitscanWeapon>());
        }

        private static void SetArray(Component target, string property, UnityEngine.Object[] values)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(property);
            if (prop == null)
            {
                Debug.LogError($"[SceneBuilder] {target.GetType().Name} has no array field '{property}'.");
                return;
            }
            prop.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++)
                prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------------ materials & layers

        private static string PickShader()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline ?? GraphicsSettings.defaultRenderPipeline;
            if (pipeline != null)
            {
                var typeName = pipeline.GetType().FullName ?? string.Empty;
                if (typeName.Contains("Universal")) return "Universal Render Pipeline/Lit";
                if (typeName.Contains("HDRenderPipeline")) return "HD Pipeline/HD Lit";
            }
            return "Standard";
        }

        private static Material GetMaterial(string key, Color color)
        {
            if (Materials.TryGetValue(key, out var cached) && cached != null) return cached;

            var path = MaterialFolder + "/" + key + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shaderName = PickShader();
                var shader = Shader.Find(shaderName) ?? Shader.Find("UI/Default");
                if (shader == null)
                {
                    Debug.LogError($"[SceneBuilder] no shader available for '{key}', leaving renderer default.");
                    return null;
                }
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            SetColor(material, color);
            EditorUtility.SetDirty(material);
            Materials[key] = material;
            return material;
        }

        private static void SetColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            else if (material.HasProperty("_Color")) material.SetColor("_Color", color);
        }

        /// <summary>
        /// Gives the arena a real sky and lets ambient light come from it, instead of a flat
        /// background color with one hard directional light.
        /// </summary>
        private static void ConfigureSkybox()
        {
            var shader = Shader.Find("Skybox/Procedural");
            if (shader == null)
            {
                Debug.LogWarning("[SceneBuilder] Skybox/Procedural not found, keeping background color.");
                return;
            }

            var path = MaterialFolder + "/Skybox.mat";
            var sky = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (sky == null)
            {
                sky = new Material(shader);
                AssetDatabase.CreateAsset(sky, path);
            }

            // Late afternoon and deliberately bright. The menu camera looks five degrees downward, so
            // the lower hemisphere of this sky is literally the menu backdrop; it has to read as lit
            // haze, not as a void. Sun glow comes from the disc itself, everything else stays matte.
            sky.SetFloat("_SunSize", 0.028f);
            sky.SetFloat("_SunSizeConvergence", 5f);
            sky.SetFloat("_AtmosphereThickness", 1.1f);
            sky.SetFloat("_Exposure", 1.35f);
            sky.SetColor("_SkyTint", new Color(0.34f, 0.48f, 0.72f));
            sky.SetColor("_GroundColor", new Color(0.30f, 0.34f, 0.40f));
            // The procedural sky takes its disc colour from the scene's sun light, so _Sun only
            // exists on some pipeline versions; setting a missing property would log a warning.
            if (sky.HasProperty("_Sun")) sky.SetColor("_Sun", new Color(1f, 0.82f, 0.62f));
            EditorUtility.SetDirty(sky);

            RenderSettings.skybox = sky;
            RenderSettings.ambientMode = AmbientMode.Skybox;
        }

        /// <summary>
        /// Depth squared fog in the sky's horizon colour, so the far wall dissolves into the same tone
        /// the skybox paints behind it instead of ending in a hard edge. Kept faint and light: a dark
        /// dense fog is indistinguishable from an unlit arena.
        /// </summary>
        private static void ConfigureFog()
        {
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.36f, 0.45f, 0.58f);
            RenderSettings.fogDensity = 0.0055f;
        }

        /// <summary>
        /// One realtime probe covering the arena. Baked probes need lighting data this project never
        /// generates headlessly, and without any probe at all the Standard shader samples a black
        /// reflection cubemap, which makes the glossy ground reflect nothing.
        ///
        /// Refresh is handed to ReflectionProbeRefresher: EveryFrame would re-render six cubemap
        /// faces per frame for a level that only changes where the player stands.
        /// </summary>
        private static void BuildReflectionProbe(GameObject world)
        {
            var probeGo = NewGo("Arena Reflection Probe", world.transform);
            probeGo.transform.localPosition = new Vector3(0f, 2.4f, 0f);

            var probe = probeGo.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Realtime;
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.NoTimeSlicing;
            probe.resolution = 256;
            probe.hdr = true;
            probe.intensity = 1f;
            probe.boxProjection = false;
            probe.center = Vector3.zero;
            probe.size = new Vector3(64f, 14f, 64f);
            probe.nearClipPlane = 0.3f;
            probe.farClipPlane = 90f;
            probe.shadowDistance = 40f;
            // The viewmodel is glued to the eye camera; letting it into the cubemap smears the
            // gloves across every reflective surface in the arena.
            probe.cullingMask = ~(1 << _viewModelLayer);

            probeGo.AddComponent<ReflectionProbeRefresher>();
        }

        /// <summary>
        /// The glass used on the pane props. Almost clear face-on, so what sells it is the probe
        /// reflection sliding over the surface as the player moves.
        /// </summary>
        private static void BuildGlassMaterial()
        {
            _glass = GetShaderMaterial("Glass", "NeonRelay/Neon Glass", material =>
            {
                material.SetColor("_Color", new Color(0.30f, 0.72f, 1f, 0.16f));
                material.SetColor("_Glow", new Color(0.28f, 0.86f, 1f));
                // Kept under the bloom threshold: the pane should read as a reflection, not as a lamp.
                material.SetFloat("_Edge", 0.55f);
            });
        }

        /// <summary>
        /// Same asset-caching contract as GetMaterial, but for the hand written shaders, which the
        /// arena needs with per-material property values rather than per-colour duplicates.
        /// </summary>
        private static Material GetShaderMaterial(string key, string shaderName,
            Action<Material> configure)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogWarning($"[SceneBuilder] shader '{shaderName}' not found; '{key}' keeps defaults.");
                return null;
            }

            var path = MaterialFolder + "/" + key + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            configure?.Invoke(material);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void AddGlassPane(GameObject world, Transform block)
        {
            var position = block.localPosition + Vector3.up * (block.localScale.y * 0.5f + 0.62f);
            // Turn the pane to face out of the arena, so it catches the sky rather than presenting the
            // same edge-on silhouette from every angle.
            var yaw = Mathf.Atan2(block.localPosition.x, block.localPosition.z) * Mathf.Rad2Deg;
            AddDecorBox(world, "GlassPane", position,
                new Vector3(block.localScale.x * 0.82f, 1.24f, 0.08f), _glass, yaw);
        }

        /// <summary>
        /// A decorative cube: collider removed so trim never blocks movement or a shot, and put on the
        /// Environment layer so the probe and the world camera both still see it.
        /// </summary>
        private static GameObject AddDecorBox(GameObject world, string name, Vector3 position,
            Vector3 scale, Material material, float yaw = 0f)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(world.transform, false);
            go.transform.localPosition = position;
            go.transform.localScale = scale;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);

            var collider = go.GetComponent<Collider>();
            if (collider != null) UnityEngine.Object.DestroyImmediate(collider);

            var renderer = go.GetComponent<Renderer>();
            if (material != null && renderer != null) renderer.sharedMaterial = material;
            SetLayerRecursive(go, _environmentLayer);
            return go;
        }

        private static void ApplyMaterial(GameObject target, string key, Color color)
        {
            var renderer = target.GetComponent<Renderer>();
            if (renderer == null) return;
            var material = GetMaterial(key, color);
            renderer.sharedMaterial = material;

            if (Surfaces.TryGetValue(key, out var surface)) TuneSurface(material, surface);
        }

        /// <summary>
        /// Specular profile for a texture-baked model material. The FBX slots arrive flat, and the
        /// rifle is the one thing in the frame that should read as machined steel.
        /// </summary>
        private static (float Metallic, float Smoothness, Color Emission) SurfaceProfileFor(string name)
        {
            if (Surfaces.TryGetValue(name, out var arena)) return arena;
            if (name.Contains("AK47") || name.Contains("Rifle") || name.Contains("Gun"))
                return (0.58f, 0.66f, Color.black);
            if (name.Contains("Hands")) return (0.04f, 0.3f, Color.black);
            return (0.08f, 0.42f, Color.black);
        }

        /// <summary>
        /// Writes the Standard shader's specular response by hand. The arena materials are created
        /// from code and never touched in the Inspector, so without this everything sits on the
        /// shader defaults: 0.5 smoothness and no metal, which reflects nothing anywhere.
        /// </summary>
        private static void TuneSurface(Material material,
            (float Metallic, float Smoothness, Color Emission) surface)
        {
            if (material == null) return;

            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", surface.Metallic);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", surface.Smoothness);

            if (surface.Emission.maxColorComponent > 0f)
            {
                // Emission only reaches the bloom pass while the keyword is on, and the colour has to
                // sit above 1 to survive into the HDR target the camera renders to.
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", surface.Emission);
            }
            else if (material.IsKeywordEnabled("_EMISSION"))
            {
                material.DisableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", Color.black);
            }

            EditorUtility.SetDirty(material);
        }

        private static void SetLayerRecursive(GameObject target, int layer)
        {
            if (layer < 0) return;
            target.layer = layer;
            var transforms = target.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
                transforms[i].gameObject.layer = layer;
        }

        /// <summary>Layers 0-7 are built in, so custom layers start at 8.</summary>
        private static int GetOrAddLayer(string name)
        {
            var existing = LayerMask.NameToLayer(name);
            if (existing >= 0) return existing;

            var tagManager = new SerializedObject(
                AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");

            for (int i = 8; i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(element.stringValue)) continue;

                element.stringValue = name;
                tagManager.ApplyModifiedPropertiesWithoutUndo();
                AssetDatabase.Refresh();
                Debug.Log($"[SceneBuilder] added layer '{name}' at index {i}.");
                return i;
            }

            Debug.LogError($"[SceneBuilder] no free layer slot for '{name}'.");
            return 0;
        }
    }
}
