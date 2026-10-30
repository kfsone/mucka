# Font setup

How big the text is, and the pages that ask. The UI font scale has a step for every whole number
from `UiFontScale.Min` to `UiFontScale.Max`, the side panel widens with it, and on first launch a
plain size picker and then the Text Wizard ask the player to choose the UI step, the Game Font and
the chat colours, with live examples. Separately, the app window opens before anything slow
happens at start-up, including `-import`.

## The problem

EVIDENCE A Mac player reported the side panel text as "tiny", borderline unreadable, and could not
find the settings button for the same reason. A screenshot of his screen looked normal on the
operator's low-dpi Windows monitor.

FACT `Platforms/MacCatalyst/Info.plist` declares `UIDeviceFamily` 2 (iPad). A Catalyst app with
the iPad idiom runs "Scaled to Match iPad": macOS draws the whole interface at 77%. An authored
11pt label is about 8.5pt on the Mac. A Retina screenshot carries two pixels per point, so viewed
1:1 on a low-dpi monitor it shows at roughly twice its real size, which is why it looked fine.

FACT Every span in the docked side panel was `Tight=True`, held at the old `TightMaxScale` of 1.15.
No step of the old three could make the side panel larger than that.

DECISION The Mac idiom (`UIDeviceFamily` 6, "Optimize Interface for Mac") is rejected for now: it
restyles every control and moves MAUI onto a less-trodden path on a platform nobody here can test
day to day. The 77% is compensated in-app by the step the player chooses.

## Steps

FACT `UiFontScale` has a step for every whole number from `UiFontScale.Min` to `UiFontScale.Max`,
with `UiFontScale.Normal` (0) multiplying by exactly 1. Each step is `UiFontScale.StepRatio` times the
one below, so +2 is about the old Large and +4 about the old Larger, and +4 undoes the Mac's 77%.

FACT The step lives under the ini key `UiFontScale.IniKey` (`uifontscale`). The old key
`UiFontScale.LegacyIniKey` (`uifontstep`, values 0-2) is translated once, when `SettingsStore` loads
the file, by `UiFontScale.MigrateLegacyKey` through `UiFontScale.FromLegacyStep`, and removed. A new
key, rather than reusing the old one, because an old value 1 or 2 read as a new step would silently
shrink a player's text.

FACT `Tight=True` caps the scale at `UiFontScale.TightMaxScale`, which is the +4 scale. It is used
by the status bar and the F-key toolbar, whose widths are tiered by terminal columns. Above +4 those
two stop growing; above about +4 the wide status bar can run out of room before the `effcols < 76`
compact switch takes over. That is accepted: the alternative is a status bar that never grows on
the Mac.

FACT The side panel is not tight. Its width is `CombatRailResize.SidePanelWidthAt` of
`UiFontScale.PanelScale`: `CombatRailResize.SidePanelWidthDp` times the step's scale for steps above
Normal, and the authored width at and below Normal, so the window's
minimum width rises with it. Every reader of the panel's width takes the live step:
`SidePanelBorder` in `GamePage.xaml` through `SidePanelWidthExtension`, the window arithmetic in
`CombatRailResize` through its `sidePanelScale` arguments, and `ConnectViewModel`'s terminal width.
A step change mid-session re-fits the window's minimum width.

## First launch: the size picker, then the Text Wizard

### The size picker

FACT `InterfaceSizePage` is the first thing a new player sees: the logo filling the window behind a
heading, "Mucka text size", one line saying what it is for, and a scrolling column of the
same line - "Double-click here if this text is a good size" - at every UI step, smallest at the
top. A single tap highlights a row; a double tap picks it; the arrows above and below the column
move the highlight `InterfaceSizePage.ArrowScrollRows` rows; Enter or Escape picks the highlighted row. It opens with the
`FontSetup.Initial` step highlighted and centred.

DECISION The rows are drawn at the who list's name size and font (`InterfaceSizePage.SampleSize`, `InterfaceSizePage.SampleFont`), not at a
body size: it is the smallest text the game window draws that matters, and the text the Mac player
could not read, so the row a player finds comfortable is the setting that makes the who list
comfortable. Nothing on the page follows the live step, so nothing moves while the player looks.

FACT After a pick the app runs at that step, and on the desktop the window grows (never shrinks)
towards the size the Text Wizard needs at that step, `ConnectPage.FitWindowForWizard`, capped to
most of the screen.

### The Text Wizard

FACT `FontSetupPage` is headed "Mucka Text Wizard (pun intended)" with Skip / Cancel and "Looks
good" beside the heading, "Choose basic display settings to start!" below it, and the logo filling
the window faintly behind. Top to bottom:

- "Mucka text size": the UI step, shown as "Size: Default" or its signed offset ("+3", "-2":
  `UiFontScale.Name`), set with a slider running from "Tiny" to "Large";
- a mock of the game window, a rough frame titled "How it will look", filling the space between;
- "Game text size": the Game Font (`FontSetup.MinGameFontPx` to `FontSetup.MaxGameFontPx`, named by
  `FontSetup.GameFontName`), shown and set the same way;
- "Chat colours": a drop-down of `ChatTheme.All`, with "Your own colours" first when the stored
  colours are not a theme's own.

FACT The mock holds a status bar line built with the game page's own `StatLabelStyle` and
`StatMinorFontSize`; a who list of real `WhoEntry` objects through the game page's own
`WhoEntryTemplate`, as wide as the side panel would be at the chosen step; and a real `TerminalView`
at the chosen Game Font and colours, showing `SampleScreenScript` - a room and some chat written as
the server's bytes and played through `MudStreamParser`, so its styles and chat parts are the
parser's own, with no prompt after its last line. The terminal fills its part of the mock, so at a
large Game Font the top of the sample scrolls out of view; the window growing after the picker is
what keeps that rare.

FACT While the pointer is on the "Mucka text size" row the status bar and the who list are outlined
in yellow and the terminal fades to `FontSetupPage.FadedOpacity`; on the game rows the terminal is outlined and the rest
fades. The game rows keep it while the theme drop-down is open. Each outline is a border of `FontSetupPage.OutlineStroke` that is
always there in the colour behind it, so showing it moves nothing.

FACT The UI step applies to the whole app as the buttons are pressed (`UiFontScaleState.Step` is
live), so the mock's status bar and who list follow it; the page's own words and buttons stay at
the step it opened with, and the size rows sit above and below the mock, so no button moves under
the pointer. "Looks good" keeps the new sizes and theme. The other button reads "Skip" when the
page opened by itself and "Cancel" when the player opened it; either puts back the sizes the page
opened with - after the picker, the picked step - and leaves the colours alone. The page returns a
`FontSetupResult` and does not write the ini itself; its caller does. A theme picked there is
saved as that theme with its own four colours, the faces left as they were. While the page is up
the window's title is the page's own.

The styles and the template live in `Resources/Styles/Chrome.xaml`, merged into the application's
resources, so the examples cannot drift from what the game page draws.

RULE These pages' words are for a player who has never seen Mucka and may not be technical: plain
and human, no jargon ("UI", "font", "terminal", "px"), and not themed. "Tap", not "press". Settings
is always named with its icon, `Glyph.Settings` then "Settings". Settings uses the same names for
the two sizes ("Mucka Text Size", "Game Text Size"), the same Tiny-to-Large sliders and the same
"Size: Default" / offset labels.
enforced_by: none: judgement

DECISION The two sizes are "Mucka text size" and "Game text size": one word tells them apart,
whose text it is, and the same pair works in Settings. Rejected: "UI Font" and "Interface text"
(jargon), "Menus and panels" (lists two parts, leaves out the status bar and settings, says nothing
about size), "Screen text" (the game's text is on the screen too), "Controls" (reads as key
bindings in a game), "App text" (a phone word, and could mean the game's text), "Buttons and
panels" (concrete but partial; kept in the hint instead). A value on its own ("Default", "+2")
did not read as a size to a first-time viewer, and - / + buttons did not say what they changed, so
each is a slider from "Tiny" to "Large" with "Size: Default" (or "+2") beside it.

DECISION The examples are real components fed mock data. Hand-drawn look-alikes were rejected: they
go stale the first time the real thing is restyled, and then the page lies. Moving the whole status
bar out of `GamePage.xaml` was also rejected: its bindings run through `GameViewModel` and no test
checks a XAML binding path.

### When they appear

FACT The picker and then the Text Wizard open on their own when the global key `FontSetup.DoneKey`
(`fontsetup`) is absent from `[settings]`, on Windows and the Mac. Closed either way, the wizard
saves what it shows and writes the key, so they open by themselves once. On the profile page they
come after any `-import` result and before the Mac password-storage question, one modal at a time,
and before a direct-connect launch connects.

FACT Android and iOS never open them and show neither button: the wizard's examples are laid out
for a desktop window, and nobody has looked at them at phone width. Both run at their stored step,
and their side panel keeps `CombatRailResize.SidePanelWidthDp` at every step
(`SidePanelWidthConverter`): on a phone a panel widened with the step would take the screen, so its
text scales and may wrap.

FACT The Text Wizard alone opens again from the "Text size" button at the bottom of the profile
page's left column, and from the "Mucka Text Wizard" button at the top of the Display tab of Settings,
above the two size sliders. Opened from Settings, the choice goes back
into the settings editor's own fields and is saved by the editor's Save, so the editor cannot
overwrite it. A dropped session closes it together with the editor (Invariant #0's modal exception,
and a drop outranks any modal).

### Starting values

RULE Err on the side of oversized. A player who cannot read the first screen cannot find the setting
that fixes it.
enforced_by: FontSetupTests

FACT `FontSetup.Initial` decides what the picker highlights first and what the app runs at before
it is answered:

- Nothing stored (a fresh install): the UI step is `FontSetup.FreshStepMac` on the Mac and
  `FontSetup.FreshStepOther` on Windows, and the Game Font is `FontSetup.FreshGameFontMac` on the Mac
  and the usual default on Windows.
- Values stored but never answered (an existing player): their own values, except that on the Mac
  the UI step is at least `FontSetup.MacBaselineStep`, the step that undoes the 77%.
- Answered: their own values, untouched.

## The window

FACT `App.CreateWindow` returns at once with a blank `StartupPage` inside the window's one
NavigationPage. On a background task `StartupSequence` runs the `-import` merge if one was asked for
(the page says so while it runs), then reads the font state. Back on the UI thread it applies the
UI step and puts the profile page in the start-up page's place. Nothing on that path opens the
database before the import: the database is first opened by a connection or by the Score page,
both of which need the profile page.

FACT `StartupImport` runs the merge, and the profile page reports its outcome in an alert, on both
desktop platforms.

EVIDENCE On Windows, `Window.Title` reached the native window - the taskbar and the native window
text read "Awlie@MUD2 UK mucka 0.23.6.1" - while MAUI's default title bar drew no text. The window
therefore carries a MAUI `TitleBar` whose title is bound to `Window.Title`.
