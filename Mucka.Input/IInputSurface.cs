namespace Mucka.Input;

/// <summary>
/// The entire vocabulary available for touching the real command-input control. Four members, and
/// that is the point: this interface is the wall.
///
/// <para><b>Read this before adding anything to it.</b> Every past failure of the command box came
/// from code that could reach the native control and did something reasonable-looking with it -
/// forced a layout pass, read its text from a key handler, wrote to it from a notification chain.
/// The fix is not better comments, it is that the app side of the wall cannot see a
/// <c>TextBox</c> at all: it sees this. If a new feature seems to need a fifth method here, that is
/// the signal to ask whether the feature belongs in the input path at all - the answer has so far
/// always been no.</para>
///
/// <para>Implementations are platform adapters and must be trivial: each member is a direct property
/// get/set on the control and nothing else. No measuring, no layout, no scheduling, no logic. The
/// adapter is the one place in the app allowed to name the platform control, so it is also the one
/// place where a mistake is invisible to this project's tests - keep it too small to hide a bug
/// in.</para>
/// </summary>
public interface IInputSurface
{
    /// <summary>The text the user has typed, right now. Called only from the accept path.</summary>
    string Text { get; }

    /// <summary>
    /// Replaces the text and puts the caret at <paramref name="caretPosition"/>. Called only via
    /// <see cref="CommandInput"/>, never directly by a feature.
    ///
    /// <para>The caret is a parameter rather than always-at-the-end because the two real callers want
    /// different things: history recall continues at the end of the recalled command, while inserting
    /// a clicked name or a reply prefix leaves the caret just after the prefix so the player types the
    /// rest. Expressing that here is what stops a feature reaching for the control to do it itself -
    /// which is how the last direct writer survived the first pass of this boundary.</para>
    /// </summary>
    void SetText(string text, int caretPosition);

    /// <summary>Empties the control. Separate from <see cref="SetText"/> with an empty string so the
    /// common case cannot be mistyped, and so an adapter can make it the cheapest thing it does.</summary>
    void Clear();

    /// <summary>Returns keyboard focus to the control - Invariant #0's whole subject. Fire and
    /// forget: implementations may defer, and must be a no-op when focus is already here.</summary>
    void Focus();
}
