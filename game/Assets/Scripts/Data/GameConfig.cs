using UnityEngine;

namespace Game.Data
{
    /// <summary>
    /// All gameplay tuning lives here instead of being hardcoded in controllers,
    /// so designers can retune without touching code.
    /// </summary>
    [CreateAssetMenu(fileName = "GameConfig", menuName = "Game/Config")]
    public sealed class GameConfig : ScriptableObject
    {
        [Header("Locomotion")]
        public float walkSpeed = 3.0f;
        public float runSpeed = 6.0f;
        public float acceleration = 16f;

        [Header("Jump")]
        public float jumpHeight = 2.2f;
        public float gravity = -26f;
        public float coyoteTime = 0.12f;
        public float jumpBufferTime = 0.15f;
        public float jumpCutMultiplier = 0.45f;

        [Header("Dash")]
        public float dashSpeed = 19f;
        public float dashDuration = 0.18f;
        public float dashCooldown = 0.75f;

        [Header("Survival")]
        public float maxHealth = 100f;
        public float hazardDamage = 20f;
        public float hazardDamageInterval = 0.6f;

        [Header("Scoring")]
        public float collectibleScore = 25f;
        public int collectibleCount = 8;

        [Header("Rifle")]
        public float fireInterval = 0.1f;
        public int magazineSize = 30;
        public float reloadDuration = 2.1f;
        public float aimRange = 120f;
        // Cone in degrees. The crosshair gap is drawn from this number, so what the player sees is
        // exactly how far a shot may drift off the aim point.
        public float spreadPerShot = 0.32f;
        public float maxSpread = 2.2f;
        public float spreadRecovery = 3.4f;

        [Header("Online")]
        // The lobby registry on the VPS. Kept here so moving the box is a scene edit, not a rebuild.
        public string lobbyServerUrl = "http://166.1.144.235:8791";

        [Header("First person view")]
        public float eyeHeight = 1.62f;
        public float eyeForwardOffset = 0.14f;
        public float cameraFieldOfView = 72f;
        public float minPitch = -75f;
        public float maxPitch = 75f;
        public float initialYaw = 0f;

        [Header("Character")]
        public float bodyTurnSpeed = 540f;

        [Header("Menu showcase")]
        public float menuCameraDistance = 4.2f;
        public float menuCameraHeight = 1.35f;
        public float menuCameraYaw = 180f;
        public float menuCameraPitch = -5f;
        public float menuFieldOfView = 38f;
        public float cameraFollowSpeed = 8f;

        [Header("World")]
        public float respawnY = -25f;

        public static GameConfig CreateDefault()
        {
            var config = CreateInstance<GameConfig>();
            config.name = "GameConfig (runtime default)";
            return config;
        }
    }
}
