#if WINDOWS
namespace Mucka.Pages;

/// <summary>
/// The platform adapter behind <see cref="Mucka.Input.IInputSurface"/>: the ONE place in the
/// application permitted to name the native command-input control for text operations.
///
/// <para><b>This file is the breach in the wall, deliberately and minimally.</b> Everything else
/// about the command box lives in Mucka.Input, which has no platform types at all and therefore
/// cannot touch a <c>TextBox</c> even by accident. Something has to bridge that gap; this is it, and
/// it is four one-line members long so that there is nowhere in it for a mistake to hide. Every past
/// failure of the command box was code that could reach the control and did something
/// reasonable-looking with it - forced a layout pass, read its text from a key handler, wrote to it
/// from a notification chain. None of those could be written here without being glaringly out of
/// place.</para>
///
/// <para><b>Rules for this file, which are not negotiable:</b> each member is a direct property
/// get/set and nothing else. No measuring, no <c>UpdateLayout</c>, no scrolling, no scheduling, no
/// conditionals beyond a null check, no logging. Work belongs on the far side of
/// <see cref="Mucka.Input.InputGate"/>, which is reachable from everywhere that legitimately needs
/// it. If a change to this file needs a second statement in a method, that is the moment to stop and
/// ask what it is really trying to do.</para>
///
/// <para>The control is fetched through a callback rather than held, because MAUI can recreate a
/// platform view at any time (see GamePage.OnInputHandlerChanged). Holding it would mean this adapter
/// could quietly end up writing to a discarded control - which is both a lost keystroke and, for a
/// torn-down WinUI visual, the RO_E_CLOSED crash class this codebase has met before.</para>
/// </summary>
internal sealed class CommandInputSurface(
    Func<Microsoft.UI.Xaml.Controls.TextBox?> box,
    Action focus) : Mucka.Input.IInputSurface
{
    public string Text => box()?.Text ?? string.Empty;

    public void SetText(string text, int caretPosition)
    {
        if (box() is not { } tb)
            return;
        tb.Text = text;
        // Set explicitly: WinUI parks the caret at 0 whenever Text is assigned programmatically, so
        // the position the caller asked for has to be re-applied every time.
        tb.SelectionStart = caretPosition;
    }

    public void Clear()
    {
        if (box() is { } tb)
            tb.Text = string.Empty;
    }

    /// <summary>Delegated to the page, which owns the focus dance Invariant #0 describes (deferred a
    /// dispatcher tick to beat WinUI's post-click focus settle, and a no-op when focus is already
    /// here). Not reimplemented: there must be exactly one focus policy.</summary>
    public void Focus() => focus();
}
#endif
