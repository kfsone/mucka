using Mucka.Terminal;

namespace Mucka.Terminal.Tests;

public class ScrollbackKeysTests
{
    private const int W = 0x57;
    private const int F5 = 0x74;
    private const int Letter = 0x45;

    /// <summary>The defect this table exists to close: Ctrl+W (wield the alternate weapon) and every
    /// F-key macro were swallowed in scrollback, because the old handler knew a hand-picked subset of
    /// the hotkeys. A registered hotkey now always reaches its handler.</summary>
    [Theory]
    [InlineData(W, true)]
    [InlineData(F5, false)]
    [InlineData(ScrollbackKeys.Escape, false)]
    public void ARegisteredHotkey_PassesThrough_WhateverTheKey(int key, bool control)
        => Assert.Equal(ScrollbackKeyAction.PassToHotkey, ScrollbackKeys.Route(key, control, isHotkey: true));

    [Fact]
    public void TheScrollbackKeys_DoWhatTheySay()
    {
        Assert.Equal(ScrollbackKeyAction.ScrollToTop, ScrollbackKeys.Route(ScrollbackKeys.Home, false, false));
        Assert.Equal(ScrollbackKeyAction.ScrollToBottom, ScrollbackKeys.Route(ScrollbackKeys.End, false, false));
        Assert.Equal(ScrollbackKeyAction.ScrollToBottom, ScrollbackKeys.Route(ScrollbackKeys.Escape, false, false));
        Assert.Equal(ScrollbackKeyAction.CopySelection, ScrollbackKeys.Route(ScrollbackKeys.C, true, false));
    }

    /// <summary>Typing has nowhere to go in scrollback; an unbound key, or C without Ctrl, is
    /// swallowed rather than handed to a control that is not there.</summary>
    [Theory]
    [InlineData(Letter, false)]
    [InlineData(ScrollbackKeys.C, false)]
    [InlineData(W, true)]
    public void AnythingElse_IsSwallowed(int key, bool control)
        => Assert.Equal(ScrollbackKeyAction.Swallow, ScrollbackKeys.Route(key, control, isHotkey: false));
}
