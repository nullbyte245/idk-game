using System;
using Game.Online;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>One lobby in the browser list. Cloned from a template the scene builder leaves inactive.</summary>
    [DisallowMultipleComponent]
    public sealed class LobbyRow : MonoBehaviour
    {
        [SerializeField] private Text lobbyName;
        [SerializeField] private Text occupancy;
        [SerializeField] private Text hostName;
        [SerializeField] private Button joinButton;

        public void Show(LobbySummary summary, Action onJoin)
        {
            gameObject.SetActive(true);

            if (lobbyName != null) lobbyName.text = summary.name;
            if (occupancy != null) occupancy.text = $"{summary.players} / {summary.maxPlayers}";
            if (hostName != null) hostName.text = string.IsNullOrEmpty(summary.hostName) ? "-" : summary.hostName;

            if (joinButton == null) return;
            joinButton.onClick.RemoveAllListeners();
            if (onJoin != null) joinButton.onClick.AddListener(() => onJoin());
            joinButton.interactable = !summary.started && summary.players < summary.maxPlayers;

            var label = joinButton.GetComponentInChildren<Text>();
            if (label != null) label.text = summary.started ? "RUNNING" : summary.players >= summary.maxPlayers ? "FULL" : "JOIN";
        }
    }
}
