using Game.Online;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>One entry in the room roster.</summary>
    [DisallowMultipleComponent]
    public sealed class LobbyPlayerRow : MonoBehaviour
    {
        [SerializeField] private Text playerName;
        [SerializeField] private Text tag;

        public void Show(LobbyPlayerInfo player, string selfClientId)
        {
            gameObject.SetActive(true);
            if (playerName != null) playerName.text = player.name;

            var label = player.isHost ? "HOST"
                : player.clientId == selfClientId ? "YOU"
                : string.Empty;
            if (tag != null) tag.text = label;
        }
    }
}
