using System;
using System.Collections.Generic;
using Game.Core;
using Game.Services;
using UnityEngine;

namespace Game.UI
{
    public static class UIScreenIds
    {
        public const string MainMenu = "ui.menu";
        public const string Hud = "ui.hud";
        public const string Pause = "ui.pause";
        public const string GameOver = "ui.gameover";
        public const string Settings = "ui.settings";
        public const string LobbyList = "ui.lobbies";
        public const string CreateLobby = "ui.createlobby";
        public const string LobbyRoom = "ui.room";
    }

    public enum ScreenKind { Exclusive, Overlay }

    /// <summary>
    /// Screens own their own widgets and never talk to each other; UIService decides which screen
    /// is visible for a given phase, and screens only react to open/close.
    /// </summary>
    public abstract class ScreenBase : MonoBehaviour
    {
        [SerializeField] protected CanvasGroup group;

        public abstract string Id { get; }
        public virtual ScreenKind Kind => ScreenKind.Exclusive;
        public bool IsOpen => gameObject.activeSelf;

        protected CanvasGroup Group => group != null ? group : GetComponent<CanvasGroup>();

        public virtual void Open()
        {
            gameObject.SetActive(true);
            var cg = Group;
            if (cg != null)
            {
                cg.alpha = 1f;
                cg.interactable = true;
                cg.blocksRaycasts = true;
            }
            OnOpen();
        }

        public virtual void Close()
        {
            OnClose();
            var cg = Group;
            if (cg != null)
            {
                cg.interactable = false;
                cg.blocksRaycasts = false;
            }
            gameObject.SetActive(false);
        }

        protected virtual void OnOpen() { }
        protected virtual void OnClose() { }
    }

    public sealed class UIService : IService
    {
        private readonly Dictionary<string, ScreenBase> _screens = new Dictionary<string, ScreenBase>();
        private EventBus _events;
        private IDisposable _phaseSubscription;

        public void OnRegister(IServiceRegistry registry)
        {
            _events = registry.Get<EventBus>();
            _phaseSubscription = _events.Subscribe<PhaseChangedEvent>(OnPhaseChanged);
        }

        public void OnDispose()
        {
            _phaseSubscription?.Dispose();
            _phaseSubscription = null;
            _screens.Clear();
        }

        public void Register(ScreenBase screen)
        {
            if (screen == null) return;
            _screens[screen.Id] = screen;
            screen.Close();
        }

        public bool TryGetScreen<T>(out T screen) where T : ScreenBase
        {
            foreach (var pair in _screens)
            {
                if (pair.Value is T typed)
                {
                    screen = typed;
                    return true;
                }
            }
            screen = null;
            return false;
        }

        public void Open(string id)
        {
            if (!_screens.TryGetValue(id, out var screen)) return;

            if (screen.Kind == ScreenKind.Exclusive)
                CloseAllExclusiveExcept(id);

            screen.Open();
        }

        public void Close(string id)
        {
            if (_screens.TryGetValue(id, out var screen)) screen.Close();
        }

        public void OpenOverlay(string id) => Open(id);

        public void CloseOverlay(string id) => Close(id);

        public bool IsOpen(string id) => _screens.TryGetValue(id, out var screen) && screen.IsOpen;

        private void CloseAllExclusiveExcept(string keepId)
        {
            foreach (var pair in _screens)
            {
                if (pair.Key == keepId) continue;
                if (pair.Value.Kind == ScreenKind.Exclusive) pair.Value.Close();
            }
        }

        /// <summary>Single place that maps a game phase onto visible screens.</summary>
        private void OnPhaseChanged(PhaseChangedEvent args)
        {
            switch (args.Current)
            {
                case GamePhase.Menu:
                    OpenAll(UIScreenIds.MainMenu);
                    break;

                case GamePhase.Lobbies:
                    OpenAll(UIScreenIds.LobbyList);
                    break;

                case GamePhase.Room:
                    OpenAll(UIScreenIds.LobbyRoom);
                    break;

                case GamePhase.Playing:
                    OpenAll(UIScreenIds.Hud);
                    break;

                case GamePhase.Paused:
                    OpenAll(UIScreenIds.Hud, UIScreenIds.Pause);
                    break;

                case GamePhase.GameOver:
                    OpenAll(UIScreenIds.GameOver, UIScreenIds.Hud);
                    break;
            }
        }

        /// <summary>
        /// Everything the phase does not ask for is closed, so a new screen cannot be added without
        /// remembering to hide the old one. Listed overlays stay open across the switch.
        /// </summary>
        private void OpenAll(params string[] ids)
        {
            foreach (var pair in _screens)
            {
                if (Array.IndexOf(ids, pair.Key) >= 0) continue;
                pair.Value.Close();
            }
            foreach (var id in ids) Open(id);
        }
    }
}
