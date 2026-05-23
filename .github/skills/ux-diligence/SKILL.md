---
name: ux-diligence
description: "Verify Mucka UI visually by building, launching, and capturing screenshots of the running Windows app. Use when: making UI changes, adjusting layouts, styling, colours, fonts, or any visual/UX work; verifying that a feature looks correct after implementation; doing a visual regression check; confirming the app launches without a crash."
argument-hint: "optional: describe what UI aspect to verify"
---

# UX Diligence

After any change that affects the Mucka user interface, capture a screenshot of the running Windows app and view it inline to verify the result looks correct before considering the task done.

## When to Use

- After layout, colour, font, or styling changes
- After adding or modifying a page, control, or navigation element
- After a feature that has visible output (e.g. new line rendering, stats bar)
- Whenever the user asks to "check" or "verify" the UI

## Procedure

### 1. Build

```powershell
dotnet build Mucka.csproj -f net10.0-windows10.0.19041.0 -c Debug
```

Fix any build errors before proceeding.

### 2. Ask the user to navigate

The app runs locally on the user's machine. **Do not attempt to automate mouse clicks or keyboard input.**
Instead, use `ask_user` to tell them where to navigate:

> "Please open Mucka (rebuild if needed) and navigate to [the screen you want to verify]. When it's visible, press **Ctrl+`** (backtick) to take a selfie, then let me know."

The selfie is saved automatically to `%TEMP%\mucka-selfie-<timestamp>.png`, and the path is written to `%TEMP%\mucka-latest-selfie.txt`.

### 3. Read the selfie path and view it

Once the user confirms they've taken the selfie:

```powershell
$shot = Get-Content "$env:TEMP\mucka-latest-selfie.txt" -Raw
$shot = $shot.Trim()
Write-Host "Selfie: $shot"
```

Then **view** `$shot` — the `view` tool renders PNG files inline.

If the file doesn't exist (app not running or selfie not taken), fall back to the capture script:

```powershell
$shot = & .\.github\skills\ux-diligence\scripts\capture-window.ps1
```

### 4. Verify

Look at the screenshot and check:
- The expected UI element is present and correctly placed
- Colours and fonts are as intended
- No obvious layout breakage or missing content

### 5. Iterate or close

- If something looks wrong, fix the code, ask the user to rebuild and take a fresh selfie, then repeat from step 3.
- If the UI looks correct, the task is done.

## Tips

- Launch with `-logs <path>` on Windows to capture trace output to a file: e.g. `Mucka.exe -logs C:\tmp\mucka.log`
- The selfie (Ctrl+`) captures only the MAUI page content (no window chrome). For a full window shot, use the PS capture script.
- Pass `-WindowTitle` to the capture script to target a specific window title fragment (default: `"mucka"`).
- Pass `-FullScreen` to the script if the window isn't found.
