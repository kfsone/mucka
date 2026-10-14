#if MACCATALYST
using CoreAnimation;
using Foundation;
using UIKit;

namespace Mucka.Behaviors;

/// <summary>
/// Opacity fades for the Mac, run by Core Animation. An animation added to a layer is carried out by
/// the render server, off the app's UI thread, so it costs the typing path nothing - the Mac
/// counterpart of the Windows compositor animations these behaviors use.
/// </summary>
internal static class MacLayerFade
{
    private const string Key = "mucka.fade";

    /// <summary>Fades the view from <paramref name="from"/> to <paramref name="to"/> over
    /// <paramref name="seconds"/>, after <paramref name="delaySeconds"/> during which it shows
    /// <paramref name="from"/>. Replaces any fade already running on it.</summary>
    public static void Animate(UIView view, float from, float to, double seconds, double delaySeconds = 0)
    {
        var layer = view.Layer;
        layer.RemoveAnimation(Key);

        var fade = CABasicAnimation.FromKeyPath("opacity");
        fade.From = NSNumber.FromFloat(from);
        fade.To = NSNumber.FromFloat(to);
        fade.Duration = seconds;
        fade.TimingFunction = CAMediaTimingFunction.FromName(CAMediaTimingFunction.EaseInEaseOut);
        if (delaySeconds > 0)
        {
            fade.BeginTime = CAAnimation.CurrentMediaTime() + delaySeconds;
            fade.FillMode = CAFillMode.Backwards;   // shows the start value while it waits
        }

        // The layer's own value is the END state, so it is what remains once the animation is done.
        layer.Opacity = to;
        layer.AddAnimation(fade, Key);
    }

    /// <summary>Gives the view up: no fade, fully opaque.</summary>
    public static void Release(UIView view) => Set(view, 1f);

    /// <summary>Stops any fade and holds the view at <paramref name="opacity"/>.</summary>
    public static void Set(UIView view, float opacity)
    {
        view.Layer.RemoveAnimation(Key);
        view.Layer.Opacity = opacity;
    }
}
#endif
