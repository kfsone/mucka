<#
.SYNOPSIS
    Capture a screenshot of the Mucka app window (or any named window).

.PARAMETER OutputPath
    Where to save the PNG. Defaults to a timestamped file in the system temp folder.

.PARAMETER WindowTitle
    Partial title to match (case-insensitive). Defaults to "mucka".

.PARAMETER FullScreen
    Fall back to a full-screen capture if the window is not found.

.EXAMPLE
    .\capture-window.ps1
    .\capture-window.ps1 -OutputPath C:\tmp\shot.png
    .\capture-window.ps1 -WindowTitle "mucka 0.3"
#>
param(
    [string]$OutputPath   = "",
    [string]$WindowTitle  = "mucka",
    [switch]$FullScreen
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# P/Invoke-only block — no managed drawing types, so no assembly reference issues
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;

public class WinCapture {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
    [DllImport("user32.dll")] public static extern int  GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr lp);

    public struct RECT { public int L, T, R, B; }

    public static IntPtr FindByTitle(string fragment) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            if (sb.ToString().IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static bool GetRect(IntPtr hwnd, out int x, out int y, out int w, out int h) {
        x = y = w = h = 0;
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return false;
        x = r.L; y = r.T; w = r.R - r.L; h = r.B - r.T;
        return w > 0 && h > 0;
    }
}
"@

if (-not $OutputPath) {
    $ts = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = [System.IO.Path]::Combine($env:TEMP, "mucka-screenshot-$ts.png")
}

$hwnd = [WinCapture]::FindByTitle($WindowTitle)
if ($hwnd -eq [IntPtr]::Zero) {
    if ($FullScreen) {
        Write-Warning "Window '$WindowTitle' not found — capturing full screen."
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $g   = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
        $g.Dispose()
    } else {
        Write-Error "No visible window matching '$WindowTitle'. Is the app running? Use -FullScreen to capture the desktop anyway."
        exit 1
    }
} else {
    [WinCapture]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 250
    $x = $y = $w = $h = 0
    if (-not [WinCapture]::GetRect($hwnd, [ref]$x, [ref]$y, [ref]$w, [ref]$h)) {
        Write-Error "Could not retrieve window rect."
        exit 1
    }
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()
}

$bmp.Save($OutputPath)
$bmp.Dispose()

# Output just the path so callers can pipe/read it
Write-Output $OutputPath
