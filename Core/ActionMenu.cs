#if UIKIT
using UIKit;
#endif

namespace Mucka.Core;

/// <summary>A short menu of actions, the destructive one last. MAUI's action sheet puts its destructive
/// entry first on iOS; on UIKit the platform's own sheet keeps the order given and still shows that
/// entry in red. Elsewhere it is MAUI's sheet with the destructive entry as an ordinary last item.</summary>
internal static class ActionMenu
{
    /// <summary>Returns the chosen item, or null when cancelled.</summary>
    public static async Task<string?> ShowAsync(Page page, View anchor, string title, string cancel,
        string? destructive, params string[] items)
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
}
