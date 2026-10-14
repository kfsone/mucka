#if MACCATALYST
using UIKit;

namespace Mucka.Pages;

/// <summary>
/// The platform adapter behind <see cref="Mucka.Input.IInputSurface"/> on a Mac: the one place that
/// names the native text field. Same rules as the Windows adapter - each member is a direct property
/// get/set and nothing else; no layout, no scheduling, no logging. Work belongs behind
/// <see cref="Mucka.Input.InputGate"/>.
///
/// <para>The field is fetched through a callback rather than held, because MAUI can recreate a
/// platform view at any time.</para>
/// </summary>
internal sealed class MacCommandInputSurface(Func<UITextField?> box, Action focus) : Mucka.Input.IInputSurface
{
    public string Text => box()?.Text ?? string.Empty;

    public void SetText(string text, int caretPosition)
    {
        if (box() is not { } tf)
            return;
        tf.Text = text;
        // Set explicitly: assigning Text leaves the caret wherever UIKit puts it.
        if (tf.GetPosition(tf.BeginningOfDocument, caretPosition) is { } caret)
            tf.SelectedTextRange = tf.GetTextRange(caret, caret);
    }

    public void Clear()
    {
        if (box() is { } tf)
            tf.Text = string.Empty;
    }

    /// <summary>Delegated to the page, which owns the one focus policy.</summary>
    public void Focus() => focus();
}
#endif
