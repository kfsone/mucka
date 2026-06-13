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
            int fkeyNum = e.KeyCode switch
            {
                Keycode.F1  => 0,
                Keycode.F2  => 1,
                Keycode.F3  => 2,
                Keycode.F4  => 3,
                Keycode.F5  => 4,
                Keycode.F6  => 5,
                Keycode.F7  => 6,
                Keycode.F8  => 7,
                Keycode.F9  => 8,
                Keycode.F10 => 9,
                Keycode.F11 => 10,
                Keycode.F12 => 11,
                _           => -1
            };
            if (fkeyNum >= 0)
            {
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
        }
        return base.DispatchKeyEvent(e);
    }
}
