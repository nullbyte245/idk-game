using System;
using System.Collections.Generic;
using Game.Core;
using Game.Services;
using UnityEngine;

namespace Game.Player
{
    /// <summary>
    /// Collapses the head and arm bones while the run is live so the first person camera is never
    /// inside the helmet and the body's own arms do not fight the viewmodel hands. The world rifle is
    /// bone-parented to the hand, so collapsing the hand hides it too.
    /// LateUpdate is the first point after the Animator has written its bone curves, so the collapse is
    /// re-applied every frame instead of being set once.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FirstPersonHeadHider : MonoBehaviour
    {
        private static readonly string[] HeadBoneNames =
        {
            "Head", "Head_end", "Face", "Helmet", "Hat", "Hair", "Beard"
        };

        /// <summary>
        /// Everything from the elbow out on both sides. The shoulder is left alone: it carries the
        /// chest geometry, so collapsing it would pucker the torso into a dent.
        /// </summary>
        private static readonly string[] ArmBoneNames =
        {
            "UpperArm.L", "UpperArm.R", "LowerArm.L", "LowerArm.R",
            "Fist.L", "Fist.R", "Fist1.L", "Fist1.R", "Fist2.L", "Fist2.R",
            "Thumb1.L", "Thumb1.R", "Thumb2.L", "Thumb2.R", "Weapon.R"
        };

        private const float HiddenScale = 0.0001f;

        private readonly List<Transform> _bones = new List<Transform>();
        private readonly List<Vector3> _authoredScales = new List<Vector3>();

        private GameFlowService _flow;

        private void Awake()
        {
            foreach (var transform in GetComponentsInChildren<Transform>(true))
            {
                if (System.Array.IndexOf(HeadBoneNames, transform.name) < 0
                    && System.Array.IndexOf(ArmBoneNames, transform.name) < 0) continue;
                _bones.Add(transform);
                _authoredScales.Add(transform.localScale);
            }

            if (_bones.Count == 0)
                Debug.LogWarning($"FirstPersonHeadHider found no head or arm bones under {name}.");

            if (GameServices.TryGet(out GameFlowService flow)) _flow = flow;
        }

        private void LateUpdate()
        {
            if (_bones.Count == 0) return;

            // Menu and Boot show the whole character; a live run (and its pause/game-over frames) does not.
            var hide = _flow != null && !_flow.IsShowcase;

            for (var i = 0; i < _bones.Count; i++)
                _bones[i].localScale = hide ? Vector3.one * HiddenScale : _authoredScales[i];
        }
    }
}
