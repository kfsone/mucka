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
}
