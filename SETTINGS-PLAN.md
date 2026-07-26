# Settings dialog — plan

Target: parity with Dolphin's Preferences pages. Grounded in the Dolphin
Handbook (docs.kde.org/stable_kf6/en/dolphin, "Configuring Dolphin"), read
rather than recalled, and cross-referenced against what Heimdall's code
actually has today.

Nothing here is built yet. This is the scope decision.

---

## Three decisions this needs first

### 1. Settings do not go in `session.json`

`SessionState` is a snapshot of *where you were* — window geometry, tabs, split
ratio, per-tab scale. Preferences are *what you always want*. They are different
lifetimes and they conflict directly: "restore my last folders" and "always start
in Home" are both startup settings, and one of them has to win over the other on
every launch.

So: a new `settings.json` beside `session.json` in `~/.local/state/heimdall/`,
its own source-generated `JsonSerializerContext` (reflection JSON does not
survive trimming), its own version integer, and the same
atomic-write-plus-`.bak` discipline. A corrupt settings file must fall back to
defaults rather than block startup, exactly as the session does.

The two files stay separate, but startup reads settings *first*, because the
startup setting decides whether the session is consulted at all.

### 2. Only settings that control something real

Dolphin's pages contain items that correspond to features Heimdall does not have,
and in three cases to features it deliberately does not have. A dialog full of
toggles that do nothing is worse than no dialog — and it directly contradicts the
standing requirement that the UI be usable by someone with no prior knowledge of
it. Someone who ticks "Open archives as folder" and sees nothing happen has been
lied to by the application.

So the rule for this page set: **a setting ships with its feature, not before
it.** Everything below is marked accordingly.

### 3. Some Dolphin settings *are* the switch for an unbuilt parity item

Most notably "share the same view display style among all folders, or let folders
remember their own" — that toggle **is** the per-folder view properties feature
(`PARITY.md`, medium tier). The setting cannot exist before the feature; the
feature is not much use without the setting. They are one piece of work, which is
why `PARITY.md` already lists them adjacent.

Same shape for "Expandable folders" in the Details view mode: that is a tree-in-
list feature, not a preference.

---

## Page by page

Legend: **build** = ships in this work · **defer** = arrives with its feature ·
**omit** = does not apply to this project.

### General → Behavior

| Dolphin setting | Heimdall today | Call |
|---|---|---|
| Shared vs per-folder view style | not built | **defer** — this is the per-folder view properties item |
| Sorting: natural on/off, case sensitivity | `NaturalOrder.cs`, applied unconditionally | **build** — the code exists, it just has no switch |
| Show tooltips | row tooltips exist (age description) | **build** |
| Show selection marker (+/− on hover) | not built | **defer** — this is the "selection mode" parity item |
| Rename inline vs dialog | F2 → `BeginRenameCommand` | **build** once I confirm which mode it uses |
| Switch split panes with Tab | already works, unconditionally | **build** — toggle over existing behaviour |
| Turning off split closes active pane | `RememberedRightPane` restores the right side | **build**, but semantics differ from Dolphin and the difference is deliberate |

### General → Previews

| Dolphin setting | Heimdall today | Call |
|---|---|---|
| Which file types get previews | thumbnails for everything the freedesktop cache has | **build** |
| Max size for local files | no limit | **build** |
| Max size for remote files | no limit | **build** — matters here, remote mounts are a headline feature |
| Previews inside folder icons | not built | **defer** |

### General → Confirmations

| Dolphin setting | Heimdall today | Call |
|---|---|---|
| Confirm move to trash | no confirmation | **build** |
| Confirm permanent delete | confirmed, with real buttons | **build** — toggle over existing |
| Confirm closing window with multiple tabs | no confirmation | **build** |
| Default action for executable files | always opens via desktop database | **build** |

### General → Status Bar

| Dolphin setting | Heimdall today | Call |
|---|---|---|
| Show status bar | always shown | **build** |
| Show zoom slider | no slider — Heimdall uses a typed-value flyout | **omit**, the control it toggles does not exist |
| Show space information | free space is shown | **build** |

### Startup

Every item here is buildable, and this page matters more for Heimdall than for
Dolphin: "OneCommander forgets open folders on restart" is the project's founding
complaint, so the setting that governs it is the most consequential in the dialog.

| Dolphin setting | Heimdall today | Call |
|---|---|---|
| Show on startup: folder, or restore last session | always restores | **build** — restore stays the default |
| Begin in split view | restored from session | **build** |
| Show filter bar | off | **build** |
| Make location bar editable | breadcrumbs | **build** |
| Open new folders in tabs | `SingleInstance` exists; behaviour not configurable | **build** |
| Show full path in location bar | shortened against Places | **build** |
| Show full path in title bar | folder name only | **build** |

### View Modes (Icons / Compact / Details tabs)

| Dolphin setting | Heimdall today | Call |
|---|---|---|
| Icon size sliders, default and preview | per-pane `IconScale`, no global default | **build** — a default the panes start from |
| System font or custom font | reads `kdeglobals` | **build**, with the caveat below |
| Icons: text width, max lines | not configurable | **build** |
| Compact: max width | not configurable | **build** |
| Details: expandable folders | not built | **defer** |
| Details: folder size — count vs contents, recursion limit | inline metadata shows counts | **build** |
| Details: date style relative vs absolute | `AgeConverters` does relative | **build** |

Caveat on the font: everything currently derives from `kdeglobals` and the
`FontScale`/`IconScale` axes, and that is what makes the palette and metrics
follow the desktop. A custom font override has to feed the same
`PaneScale.Compute` pipeline rather than bypass it, or per-pane scaling silently
stops working for anyone who sets one.

### Navigation

| Dolphin setting | Heimdall today | Call |
|---|---|---|
| Single vs double click to open | `DoubleClick.cs`; Dolphin defers to system settings | **build** — read the system setting, allow an override |
| Open archives as folder | **deliberately not built** | **omit** — archives were dropped at your request; this would reopen that |
| Open folders during drag (spring-loaded) | not built | **defer** |

### Services

Split cleanly in two.

- **Service menus** (`.desktop` files in `servicemenus/`) — `PARITY.md` records
  these as deliberately not done, the scripts menu covering the same need.
  **Omit.**
- **Which context-menu commands are shown** — Copy To, Move To, Add to Places,
  Sort By, View Mode, Open in New Tab, Open in New Window, Copy Location,
  Duplicate. Heimdall has every one of these. **Build.** This is the useful half.
- **Version control decorations** — `PARITY.md` medium tier, not built.
  **Defer.**

### Trash

Entirely applicable and self-contained; Heimdall already implements the XDG trash
spec.

| Dolphin setting | Call |
|---|---|
| Delete files older than N days | **build** |
| Limit trash to N% of disk | **build** |
| Action at limit: warn, or delete oldest/largest | **build** |

### User Feedback

**Omit.** This is KDE's telemetry opt-in. Heimdall is a personal tool for its
author and a few friends, it sends nothing anywhere, and a page saying so would
be stranger than its absence.

---

## What this comes to

Six pages, not seven: General (four tabs), Startup, View Modes (three tabs),
Navigation, Context Menu, Trash.

Roughly 35 settings that control existing behaviour and can ship together; 7
deferred to arrive with their features; 4 omitted as not applying.

The largest single piece of new plumbing is not the dialog — it is
`settings.json` and threading a settings object through to the places that
currently hardcode these behaviours. The dialog itself is a `TabControl` and
bindings.

## Suggested order

1. `SettingsModel` + `JsonSettingsStore` + defaults, wired so nothing reads a
   hardcoded value any more. No UI. Verifiable by the app behaving identically.
2. Startup page. Highest value, smallest surface, and it exercises the
   settings-before-session ordering.
3. General's four tabs.
4. View Modes, Context Menu, Trash.
5. Per-folder view properties as its own piece, which brings the Behavior
   toggle with it.
