#if UIKIT
using UIKit;
#endif

namespace Mucka.Core;

/// <summary>A short menu of actions, the destructive one last, opened from the control that asked for
/// it.
///
/// <list type="bullet">
/// <item>Windows: a drop-down (WinUI <c>MenuFlyout</c>) under the anchor, its text at the UI font
/// step, the destructive entry red below a separator. No title: the row or button it hangs from is
/// still in view.</item>
/// <item>UIKit: the platform's own sheet, which keeps the order given, shows the destructive entry in
/// red, and on an iPad or a Mac points at the anchor. MAUI's sheet would put the destructive entry
/// first.</item>
/// <item>Elsewhere: MAUI's action sheet, titled, with the destructive entry as an ordinary last
/// item.</item>
/// </list>
///
/// <para>An item can hold a submenu: on Windows a cascading entry of the drop-down, elsewhere a second
/// sheet, with the same title, once the item is chosen. Either way the result is the submenu's chosen
/// entry.</para></summary>
internal static class ActionMenu
{
    /// <summary>Returns the chosen item, or null when cancelled.</summary>
    /// <param name="title">The sheet's title where a sheet is used; a drop-down has none.</param>
    public static Task<string?> ShowAsync(Page page, View anchor, string title, string cancel,
        string? destructive, params string[] items)
        => ShowAsync(page, anchor, title, cancel, destructive, items, new Dictionary<string, string[]>());

    /// <summary>Returns the chosen item or submenu entry, or null when cancelled.</summary>
    /// <param name="submenus">The entries under each item of <paramref name="items"/> that holds a
    /// submenu. Every entry is distinct from every item, since only the chosen text comes back.</param>
    public static async Task<string?> ShowAsync(Page page, View anchor, string title, string cancel,
        string? destructive, string[] items, IReadOnlyDictionary<string, string[]> submenus)
    {
#if WINDOWS
        if (anchor.Handler?.PlatformView is Microsoft.UI.Xaml.FrameworkElement target)
            return await DropDownAsync(target, destructive, items, submenus);
#endif
        var pick = await SheetAsync(page, anchor, title, cancel, destructive, items);
        return pick is not null && submenus.TryGetValue(pick, out var entries)
            ? await SheetAsync(page, anchor, title, cancel, null, entries)
            : pick;
    }

    private static async Task<string?> SheetAsync(Page page, View anchor, string title, string cancel,
        string? destructive, string[] items)
    {
#if UIKIT
        if (Microsoft.Maui.ApplicationModel.Platform.GetCurrentUIViewController() is { } presenter)
        {
            var chosen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sheet = UIAlertController.Create(title, null, UIAlertControllerStyle.ActionSheet);
            foreach (var item in items)
                sheet.AddAction(UIAlertAction.Create(item, UIAlertActionStyle.Default, _ => chosen.TrySetResult(item)));
            if (destructive is not null)
                sheet.AddAction(UIAlertAction.Create(destructive, UIAlertActionStyle.Destructive, _ => chosen.TrySetResult(destructive)));
            sheet.AddAction(UIAlertAction.Create(cancel, UIAlertActionStyle.Cancel, _ => chosen.TrySetResult(null)));
            // An iPad and a Mac show the sheet as a popover, which needs something to point at.
            if (sheet.PopoverPresentationController is { } popover && anchor.Handler?.PlatformView is UIView view)
            {
                popover.SourceView = view;
                popover.SourceRect = view.Bounds;
            }
            presenter.PresentViewController(sheet, true, null);
            return await chosen.Task;
        }
#endif
        var all = destructive is null ? items : [.. items, destructive];
        var pick = await page.DisplayActionSheetAsync(title, cancel, null, all);
        return pick == cancel ? null : pick;
    }

#if WINDOWS
    /// <summary>Authored size of a drop-down entry, before the UI font step.</summary>
    private const double EntrySize = 14;

    private static Task<string?> DropDownAsync(Microsoft.UI.Xaml.FrameworkElement target, string? destructive,
        string[] items, IReadOnlyDictionary<string, string[]> submenus)
    {
        var chosen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var size = Mucka.Ui.UiFontScale.Apply(EntrySize, UiFontScaleState.Current.Step);
        var flyout = new Microsoft.UI.Xaml.Controls.MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
        };

        Microsoft.UI.Xaml.Controls.MenuFlyoutItem Entry(string text)
        {
            var entry = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = text, FontSize = size };
            entry.Click += (_, _) => chosen.TrySetResult(text);
            return entry;
        }

        foreach (var item in items)
        {
            if (submenus.TryGetValue(item, out var entries))
            {
                var sub = new Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem { Text = item, FontSize = size };
                foreach (var entry in entries)
                    sub.Items.Add(Entry(entry));
                flyout.Items.Add(sub);
            }
            else
                flyout.Items.Add(Entry(item));
        }
        if (destructive is not null)
        {
            flyout.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
            var entry = Entry(destructive);
            entry.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(0xFF, 0xF8, 0x51, 0x49));
            flyout.Items.Add(entry);
        }

        // Closing is dismissal unless an entry's Click got there first; the close is queued so a
        // click that also closes the menu is not beaten to the result by its own close.
        flyout.Closed += (_, _) => target.DispatcherQueue.TryEnqueue(() => chosen.TrySetResult(null));
        try
        {
            flyout.ShowAt(target);
        }
        // An anchor that has left the visual tree (its row recycled, its page closed) cannot hold a
        // menu: nothing is chosen.
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return Task.FromResult<string?>(null);
        }
        return chosen.Task;
    }
#endif
}
