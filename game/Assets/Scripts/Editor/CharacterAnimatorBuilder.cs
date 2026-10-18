using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Game.Editor
{
    /// <summary>
    /// Builds the player's Animator Controller out of the clips that actually exist in the imported FBX
    /// files, so a missing clip drops that state instead of leaving the controller with null motions.
    ///
    ///   Unity.exe -batchmode -executeMethod Game.Editor.CharacterAnimatorBuilder.BuildAll -quit -logFile art.log
    /// </summary>
    public static class CharacterAnimatorBuilder
    {
        private const string ReportTag = "[Animator]";
        private const string ControllerFolder = "Assets/Art";
        private const string ControllerPath = ControllerFolder + "/PlayerAnimator.controller";

        private const string BodyPath = "Assets/Art/Characters/SasAgent.fbx";
        private const string DonorPath = "Assets/Art/Characters/AnimatedHuman.fbx";

        private static readonly string[] IdleNames = { "Idle", "Idle_Weapon" };
        private static readonly string[] WalkNames = { "Walk" };
        private static readonly string[] RunNames = { "Run", "Run_Weapon" };
        private static readonly string[] DashNames = { "Roll" };
        private static readonly string[] DeathNames = { "Death" };
        private static readonly string[] JumpNames = { "Jump" };

        [MenuItem("Game/Build Player Animator")]
        public static void BuildAll()
        {
            var controller = Build();
            Log(controller != null ? $"SUCCESS {ControllerPath}" : "FAILED to build controller");
        }

        public static AnimatorController Build()
        {
            var body = ClipsOf(BodyPath).ToList();
            var donor = ClipsOf(DonorPath).ToList();

            var idle = Pick(body, IdleNames);
            var walk = Pick(body, WalkNames);
            var run = Pick(body, RunNames);
            var dash = Pick(body, DashNames);
            var death = Pick(body, DeathNames);

            // A clip borrowed from another model only drives a Generic rig when its curve paths exist there.
            var jump = Pick(donor, JumpNames);
            if (jump != null && !CanBind(jump, BodyPath))
            {
                Log($"jump clip '{jump.name}' does not bind to {BodyPath}, Air state skipped");
                jump = null;
            }

            if (idle == null || walk == null || run == null)
            {
                Log($"missing locomotion clips (idle={Show(idle)} walk={Show(walk)} run={Show(run)}), aborting");
                return null;
            }

            EnsureFolder();
            AssetDatabase.DeleteAsset(ControllerPath);
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            var machine = controller.layers[0].stateMachine;

            controller.parameters = new[]
            {
                Float("Speed"), Bool("Grounded"), Trigger("Dash"), Trigger("Death")
            };

            var blend = new BlendTree
            {
                name = "LocomotionBlend",
                blendType = BlendTreeType.Simple1D,
                useAutomaticThresholds = false,
                blendParameter = "Speed"
            };
            blend.AddChild(idle, 0f);
            blend.AddChild(walk, 0.5f);
            blend.AddChild(run, 1f);
            AssetDatabase.AddObjectToAsset(blend, controller);

            var locomotion = Add(machine, "Locomotion", blend);
            machine.defaultState = locomotion;

            if (jump != null)
            {
                var air = Add(machine, "Air", jump);
                TwoWay(locomotion, air, "Grounded", requiresGrounded: false, crossFade: 0.1f);
            }

            if (dash != null)
            {
                var roll = Add(machine, "Dash", dash);
                var enter = locomotion.AddTransition(roll);
                enter.hasExitTime = false;
                enter.duration = 0.08f;
                enter.conditions = new[] { Condition(AnimatorConditionMode.If, "Dash") };

                var leave = roll.AddTransition(locomotion);
                leave.hasExitTime = true;
                leave.exitTime = 0.85f;
                leave.duration = 0.1f;
            }

            if (death != null)
            {
                var dead = Add(machine, "Death", death);
                var anyState = machine.AddAnyStateTransition(dead);
                anyState.hasExitTime = false;
                anyState.duration = 0.1f;
                anyState.conditions = new[] { Condition(AnimatorConditionMode.If, "Death") };
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            Log($"states=[{string.Join(", ", machine.states.Select(s => s.state.name))}] " +
                $"blend=[{string.Join(", ", blend.children.Select(c => $"{c.motion.name}@{c.threshold}"))}] " +
                $"air={(jump == null ? "none" : jump.name)} dash={(dash == null ? "none" : dash.name)} " +
                $"death={(death == null ? "none" : death.name)}");
            return controller;
        }

        // ------------------------------------------------------------------ clip lookup

        private static IEnumerable<AnimationClip> ClipsOf(string path)
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return Enumerable.Empty<AnimationClip>();

            return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"));
        }

        private static AnimationClip Pick(List<AnimationClip> clips, string[] names)
        {
            foreach (var name in names)
            {
                var match = clips.FirstOrDefault(c => c.name.Split('|').Last() == name);
                if (match != null) return match;
            }

            return null;
        }

        private static bool CanBind(AnimationClip clip, string bodyPath)
        {
            var body = AssetDatabase.LoadAssetAtPath<GameObject>(bodyPath);
            if (body == null) return false;

            var paths = body.GetComponentsInChildren<Transform>(true)
                .Select(t => AnimationUtility.CalculateTransformPath(t, body.transform))
                .ToHashSet();

            var bound = AnimationUtility.GetCurveBindings(clip)
                .Select(b => b.path)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct()
                .ToList();

            return bound.Count > 0 && bound.All(p => paths.Contains(p));
        }

        // ------------------------------------------------------------------ controller plumbing

        /// <summary>Adds a state and assigns its motion; AddState(string) is the overload that is stable across versions.</summary>
        private static AnimatorState Add(AnimatorStateMachine machine, string name, Motion motion)
        {
            var state = machine.AddState(name);
            state.motion = motion;
            return state;
        }

        private static void TwoWay(AnimatorState a, AnimatorState b, string parameter,
            bool requiresGrounded, float crossFade)
        {
            var forward = a.AddTransition(b);
            forward.hasExitTime = false;
            forward.duration = crossFade;
            forward.conditions = new[] { Condition(requiresGrounded ? AnimatorConditionMode.If : AnimatorConditionMode.IfNot, parameter) };

            var backward = b.AddTransition(a);
            backward.hasExitTime = false;
            backward.duration = crossFade;
            backward.conditions = new[] { Condition(requiresGrounded ? AnimatorConditionMode.IfNot : AnimatorConditionMode.If, parameter) };
        }

        private static AnimatorCondition Condition(AnimatorConditionMode mode, string parameter) =>
            new AnimatorCondition { mode = mode, parameter = parameter };

        private static AnimatorControllerParameter Float(string name) => Parameter(name, AnimatorControllerParameterType.Float);
        private static AnimatorControllerParameter Bool(string name) => Parameter(name, AnimatorControllerParameterType.Bool);
        private static AnimatorControllerParameter Trigger(string name) => Parameter(name, AnimatorControllerParameterType.Trigger);

        private static AnimatorControllerParameter Parameter(string name, AnimatorControllerParameterType type) =>
            new AnimatorControllerParameter { name = name, type = type };

        private static string Show(AnimationClip clip) => clip == null ? "null" : clip.name;

        private static void EnsureFolder()
        {
            if (AssetDatabase.IsValidFolder(ControllerFolder)) return;
            AssetDatabase.CreateFolder("Assets", "Art");
        }

        private static void Log(string message) => Debug.Log($"{ReportTag} {message}");
    }
}
