<#
.SYNOPSIS
  Synthetic typist for reproducing Mucka input-lag. Focuses the Mucka window and types a
  fixed phrase one character at a time at a fixed rate, so the INPUT_DIAG log (%TEMP%\mucka-input.txt)
  is identical and comparable across builds — no human needed to "type at 110 wpm".

.DESCRIPTION
  ~110 wpm ≈ 11 chars/sec (a 5-letter word + space = 6 keystrokes; 110*6/60 ≈ 11 cps).
  This types into whatever window is foreground, so it focuses Mucka first. It does NOT press
  Enter — we are profiling the input box only, not a server round-trip.

  Build Mucka with the diagnostics on first:
      dotnet build Mucka.csproj -p:InputDiag=true -f net10.0-windows10.0.19041.0
  Run Mucka, get to the game page, then run this script. Afterwards read %TEMP%\mucka-input.txt:
  compare each "KeyDown" line's +ms to the following "TextChanged" line, and watch for
  "UI STALL" lines landing between them. "VM.InputText set" firing once per character means the
  Text binding has regressed to TwoWay.

.PARAMETER Cps
  Characters per second. Default 11 (~110 wpm). Try 15-20 to stress it harder.

.PARAMETER Process
  Process name to focus. Default "Mucka".

.PARAMETER Text
  The phrase to type. Default is the sentence from the bug report.

.EXAMPLE
  pwsh -File tools\type-test.ps1
  pwsh -File tools\type-test.ps1 -Cps 18
#>
param(
    [double]$Cps = 11,
    [string]$Process = "Mucka",
    [string]$Text = "right now i am typing into the window and the output feels nice and smooth"
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@

$proc = Get-Process -Name $Process -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) {
    Write-Error "No '$Process' process with a visible main window found. Start Mucka and reach the game page first."
    exit 1
}

Write-Host "Focusing $Process (pid $($proc.Id)) ..."
[Win32]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
[Win32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 600   # let focus settle on the input box

Add-Type -AssemblyName System.Windows.Forms
$delayMs = [int][Math]::Round(1000.0 / $Cps)
Write-Host "Typing $($Text.Length) chars at $Cps cps (${delayMs}ms/char). Do not touch the keyboard."

# SendKeys treats these as command characters — escape any that appear in the phrase.
$special = '+^%~(){}[]'
foreach ($ch in $Text.ToCharArray()) {
    $s = [string]$ch
    if ($special.Contains($ch)) { $s = "{$ch}" }
    [System.Windows.Forms.SendKeys]::SendWait($s)
    Start-Sleep -Milliseconds $delayMs
}

Write-Host "Done. Read the timeline: `$env:TEMP\mucka-input.txt"
Write-Host "  - Compare each 'KeyDown' +ms to the next 'TextChanged' +ms (key->visible latency)."
Write-Host "  - 'UI STALL Nms' lines reveal UI-thread blocking (terminal paint / layout / fade timer)."
Write-Host "  - 'VM.InputText set' once per char == binding regressed to TwoWay."
