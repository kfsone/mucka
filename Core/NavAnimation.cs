namespace Mucka.Core;

/// <summary>Whether page pushes and pops animate. Off on a Mac, where the outgoing page stays on
/// screen for the whole transition; elsewhere the framework default (animated) is unchanged.</summary>
internal static class NavAnimation
{
#if MACCATALYST
    public const bool Animated = false;
#else
    public const bool Animated = true;
#endif

    /// <summary>Whether modal pages (settings, the text wizard, score) animate in and out. Off on a Mac
    /// for the same reason as <see cref="Animated"/>, and on iOS, where a modal slides up from the
    /// bottom of the screen: the settings page arriving that way was slow and clunky.</summary>
#if UIKIT
    public const bool ModalAnimated = false;
#else
    public const bool ModalAnimated = true;
#endif
}
