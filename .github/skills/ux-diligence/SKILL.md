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
dotnet build -f net10.0-windows10.0.19041.0 -c Debug
```

Fix any build errors before proceeding.

### 2. Launch the app

```powershell
$proc = Start-Process -PassThru (Get-ChildItem "bin\Debug\net10.0-windows10.0.19041.0\*\Mucka.exe" | Select-Object -First 1).FullName
Start-Sleep -Seconds 3   # wait for window to appear
```

### 3. Capture a screenshot

Use the bundled helper script:

```powershell
$shot = & .\.github\skills\ux-diligence\scripts\capture-window.ps1
Write-Host "Screenshot saved: $shot"
```

Then **view** the file path returned — the `view` tool renders images inline.

### 4. Verify

Look at the screenshot and check:
- The expected UI element is present and correctly placed
- Colours and fonts are as intended
- No obvious layout breakage or missing content

### 5. Iterate or close

- If something looks wrong, fix the code and repeat from step 1.
- If the UI looks correct, the task is done.

### 6. Clean up

Stop the test process when finished:

```powershell
if ($proc -and !$proc.HasExited) { Stop-Process -Id $proc.Id }
```

## Tips

- Pass `-WindowTitle` to target a specific window title fragment (default: `"mucka"`).
- Pass `-OutputPath` to control where the PNG is saved.
- Pass `-FullScreen` to capture the whole desktop if the window isn't found.
- The helper script brings the window to the foreground before capturing, so minimise other windows if you want a clean shot.
