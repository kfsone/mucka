# Settings editor reorganisation proposals

Three layouts for the Settings editor (`FkeyEditorPage.xaml`, `FkeyEditorViewModel`). None is
implemented; this document describes the shapes only.

## Current shape

Four tabs. Every setting except the F-key set is global (`GlobalSettingsMigration`).

- Settings: Columns, Volume, Stat Update (FES), beep mute (session, permanent, "hear
  it"), Advanced (Debug builds).
- Display: General (Game Font, UI Font, Default Column Width, Store Passwords Securely
  on Mac, Dreamword Pill Size), Chat colours (theme, four colour rows with own/other faces, example
  lines), Side Panel (Online and its sub-settings, Inventory, Items here, Map / Compass and its
  float).
- Hotkeys: modifier pills, import, the F-key list, "Save to profile only".
- Sounds: master row, the group/sound tree.

Defects the layouts below address:

- Columns and Default Column Width are two fields for one global concept, on two tabs.
- The chat colours are about 100 lines of the Display tab, between General and Side Panel.
- Store Passwords Securely and UI Font sit among terminal-text fields.
- Field order inside a tab is neither alphabetical nor by use.

## Proposal A: split Display, keep four-plus tabs

Tabs: General, Display, Chat, Hotkeys, Sounds.

- General: Game Font, Terminal columns, UI Font, Volume (master), Stat Update (FES),
  Mute beep (session, permanent, hear it), Store passwords securely (Mac only, last row, under a
  "This Mac" heading that is absent elsewhere), Advanced (Debug).
- Display: Side Panel (Online with sub-settings, Inventory, Items here, Map / Compass with float),
  Dreamword pill size.
- Chat: theme, the four colour rows, own/other faces, examples.
- Hotkeys, Sounds: unchanged.

Order rule: most-changed first (sizes), then audio and protocol, then per-machine items.
Fold Columns / Default Column Width into one field; the ini keeps both keys and the editor writes
one value to both until a later cleanup removes the pair.
Effort: low to medium; moves XAML blocks between existing `ScrollView`s and adds one tab-bar
entry plus one tab-active property. Risk: low; the tab bar gains a fifth entry and must still
fit the narrow window width, and the merge of the field pairs touches `ClientSettings` plumbing.

## Proposal B: task-named tabs

Tabs: Text, Panels, Chat, Audio, Hotkeys, System.

- Text: terminal font size, columns, UI font size, dreamword pill size.
- Panels: Side Panel block, Combat Rail toggles.
- Chat: theme, colours, faces, examples.
- Audio: Volume, beep mute, the Sounds tree (master row on top; the tree below).
- Hotkeys: unchanged.
- System: Stat Update (FES), anti-idle, keep screen on, Store passwords securely (Mac only), Advanced
  (Debug).

Order rule: within a tab, fields run from the most to the least commonly changed; sub-settings
indent under their parent. Mac-only items live in System, so every other tab is identical on all
platforms. Effort: medium to high; six tabs, the Sounds tab merged into Audio, anti-idle and keep
screen on moved from the connect page. Risk: medium; most XAML blocks move, the merged Audio tab has
two scrolling regions, and six tab entries need an overflow rule on the phone width.

## Proposal C: one scrolling page with section index

A single `ScrollView` with a sticky section list (Terminal, Chat colours, Side panel, Audio, Hotkeys,
This Mac) instead of tabs; the Hotkeys list keeps its own page because it has a fixed header and a
growing body.

- Terminal: font size, columns, UI font size, dreamword pill size, stat update.
- Chat colours: collapsed by default behind one heading.
- Side panel: as today.
- Audio: volume, beep mute; "Per-sound settings" opens the Sounds tree page.
- This Mac: Store passwords securely.

Order rule: sections follow how often the operator changes them; the long chat colour block is
collapsed so it stops dominating the scroll. Effort: high; replaces the tab model and
`IsSettingsTabActive`-style properties, and the Sounds tree needs a second page. Risk: highest;
a long single page puts more content between the command box and the settings the operator wants,
and the collapse state is new view state.

## Recommendation

Proposal A. It removes the three defects with the fewest moved blocks: chat colours get their own
tab, the duplicated size and column fields collapse into one each, and Mac-only items sit in a
named group at the end of General. B is the better end state if more settings arrive, and A is a
strict step toward it (the Chat tab carries over unchanged).
