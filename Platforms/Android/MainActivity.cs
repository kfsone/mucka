using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace Mucka;

// WindowSoftInputMode: the implicit default resolves to adjustPan, which slides the whole window
// up when the keyboard opens — pushing the in-game status bar off the top of the screen. Resize
// keeps the top anchored and shrinks the content instead (the pages' SafeAreaEdges="All" handles
// keyboard padding on API 35+ edge-to-edge, where adjustResize alone is ignored).
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, WindowSoftInputMode = SoftInput.AdjustResize, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e?.Action == KeyEventActions.Down)
        {
            var keyCode = (int)e.KeyCode;
            var f1 = (int)Keycode.F1;
            var f12 = (int)Keycode.F12;
            if (keyCode >= f1 && keyCode <= f12)
            {
                int fkeyNum = keyCode - f1;
                bool ctrl  = e.IsCtrlPressed;
                bool shift = e.IsShiftPressed;
                int absoluteIndex = ctrl ? 24 + fkeyNum : shift ? 12 + fkeyNum : fkeyNum;
                if (Pages.GamePage.TryFireFkeyHandler(absoluteIndex))
                    return true;
            }
            if (e.IsCtrlPressed && e.KeyCode == Keycode.D && Pages.GamePage.TryFireCtrlD())
                return true;
            if (e.IsCtrlPressed && e.KeyCode == Keycode.L && Pages.GamePage.TryFireCtrlL())
                return true;
            // Hardware Up/Down recall command history (mirrors the Windows input-box handler).
            // Intercept at the activity so the single-line Entry never sees the arrows first.
            if (e.KeyCode == Keycode.DpadUp && Pages.GamePage.TryFireHistoryUp())
                return true;
            if (e.KeyCode == Keycode.DpadDown && Pages.GamePage.TryFireHistoryDown())
                return true;
            // Escape clears the input (or closes the About overlay), mirroring the Windows handler.
            if (e.KeyCode == Keycode.Escape && Pages.GamePage.TryFireEscape())
                return true;
        }
        return base.DispatchKeyEvent(e);
    }
}
