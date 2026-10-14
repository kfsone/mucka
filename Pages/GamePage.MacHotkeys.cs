#if MACCATALYST
using Mucka.Input;

namespace Mucka.Pages;

// The game's hotkeys on a Mac. MacHotkeys (the app delegate's menu) delivers an id; this maps it to
// the same view-model calls the Windows accelerators in RegisterHotkeyAccelerators make.
public partial class GamePage
{
    // One delegate for the page's life: a method group converts to a NEW delegate each time, so
    // comparing against a fresh conversion could never match.
    private Action<string>? _macHotkeyHandler;

    private void SetUpMacHotkeys()
    {
        MacHotkeys.Handler = _macHotkeyHandler ??= OnMacHotkey;
        SetUpMacCopy();
        UIKit.UIMenuSystem.MainSystem.SetNeedsRevalidate();
    }

    private void TearDownMacHotkeys()
    {
        TearDownMacCopy();
        if (ReferenceEquals(MacHotkeys.Handler, _macHotkeyHandler))
            MacHotkeys.Handler = null;
        UIKit.UIMenuSystem.MainSystem.SetNeedsRevalidate();
    }

    private void OnMacHotkey(string id)
    {
        // The settings editor and the guided-login overlay own the keyboard while they are up.
        if (_isFkeyEditorOpen || _isGuidedLoginOverlayOpen) return;

        // Most of these act on the live game, so leave scrollback first.
        void Live(Action action)
        {
            if (Terminal.IsHistoryMode) Terminal.ScrollToBottom();
            action();
        }

        if (id.Length >= 2 && id[^1] is >= '0' and <= '9' && id.Contains('f') && !id.Contains('-'))
        {
            // f1..f12 / sf1..sf12 / cf1..cf12 -> macro slots 0-11 / 12-23 / 24-35.
            var bank = id[0] switch { 's' => 12, 'c' => 24, _ => 0 };
            if (int.TryParse(id.AsSpan(id.IndexOf('f') + 1), out var n))
            {
                _vm.SendFkeyAbsolute(bank + n - 1);
                return;
            }
        }

        switch (id)
        {
            case "ctrl-d": Live(_vm.SpeakDreamword); break;
            case "ctrl-shift-d": Live(_vm.SpeakDreamwordThen); break;
            case "ctrl-f": Live(_vm.Flee); break;
            case "ctrl-shift-f": Live(_vm.FleeThen); break;
            case "ctrl-w": Live(_vm.WieldAlternateWeapon); break;
            case "ctrl-1": Live(() => _vm.SendControlAlias(1)); break;
            case "ctrl-2": Live(() => _vm.SendControlAlias(2)); break;
            case "ctrl-3": Live(() => _vm.SendControlAlias(3)); break;
            case "ctrl-r": Live(ReplyToLastTell); break;
            case "pgup": Terminal.ScrollByPages(1); break;
            case "pgdn": Terminal.ScrollByPages(-1); break;
            case "history-up": Live(() => RecallHistory(_vm.HistoryUpCommand)); break;
            case "history-down": Live(() => RecallHistory(_vm.HistoryDownCommand)); break;
            case "escape":
                // In scrollback the box is hidden, and Esc means "back to live".
                if (Terminal.IsHistoryMode) { Terminal.ScrollToBottom(); break; }
                if (TryCloseAbout() || _vm.ScoreGraph.TryClose()) break;
                _vm.InputText = string.Empty;
                _macCommandInput?.RequestClear();
                break;
        }
    }

    private void RecallHistory(System.Windows.Input.ICommand command)
    {
        if (_macCommandInput is not { } input)
            return;
        // Behind anything already typed, as on Windows: an accepted line is added to the history on
        // the gate's drain, so Enter followed quickly by Up must recall after it, not before.
        input.Gate.Post(() =>
        {
            command.Execute(null);
            // Asserted, not inferred from a change notification: recalling a line equal to the view
            // model's current copy raises none.
            input.RequestSetText(_vm.InputText);
        });
    }
}
#endif
