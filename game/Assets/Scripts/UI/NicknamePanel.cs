using Game.Core;
using Game.Online;
using UnityEngine;
using UnityEngine.UI;

namespace Game.UI
{
    /// <summary>
    /// Bottom-right corner of the lobby screens: what other players see. The generated name is only
    /// cosmetic, but it is the one thing every lobby row shows, so it gets an edit box rather than a
    /// settings page.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class NicknamePanel : MonoBehaviour
    {
        [SerializeField] private InputField field;
        [SerializeField] private Text caption;

        private LobbyService _lobby;

        private void OnEnable()
        {
            if (!GameServices.TryGet(out _lobby)) return;

            _lobby.Changed += Refresh;
            if (field != null)
            {
                field.text = _lobby.Nickname;
                field.onEndEdit.AddListener(OnEndEdit);
            }
            Refresh();
        }

        private void OnDisable()
        {
            if (_lobby == null) return;
            _lobby.Changed -= Refresh;
            if (field != null) field.onEndEdit.RemoveListener(OnEndEdit);
            _lobby = null;
        }

        private void OnEndEdit(string value) => _lobby?.SetNickname(value);

        private void Refresh()
        {
            if (_lobby == null) return;
            if (caption != null) caption.text = $"You appear as {_lobby.Nickname}";

            // Never overwrite the field mid-typing: the roster poll runs while the player edits.
            if (field != null && !field.isFocused && field.text != _lobby.Nickname) field.text = _lobby.Nickname;
        }
    }
}
