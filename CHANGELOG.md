# Changelog

What changed in each release, from the point of view of someone using Vaktari.
Entries describe behaviour, not commits — if a change is invisible from the
outside, it belongs in the git history rather than here.

Newest first. Dates are the day the tag was cut. Versions follow
[semantic versioning](https://semver.org), with the caveat in
[Status](README.md#status): there has been no stable release, and the numbers
should not be trusted for compatibility yet.

## [Unreleased]

### Added

- **Pick which terminal opens.** Vaktari now finds the terminals installed on
  this machine — Windows Terminal, Warp, PowerShell, Command Prompt, WSL, Git
  Bash, Alacritty, WezTerm and others — and offers them under *Open terminal
  here* when there is more than one. Settings has a *Terminal* choice that F4
  follows. Before this it tried Windows Terminal, then PowerShell, then cmd, and
  opened whichever started first, with nowhere to say otherwise.

- **The desktop's own menu, under *More options*.** 7-Zip, Send to, Restore
  previous versions, Edit with Notepad++ — whatever the shell extensions on your
  machine offer. Behind one hover rather than merged in, because hosting them
  runs their code in Vaktari, and a slow or broken extension confined to that
  submenu spoils only it.
- **Administrator actions on Shift+right-click**: *Run as administrator* for
  things Windows can actually start elevated, and *Open admin terminal here*.
  Vaktari never holds administrator rights itself — these ask the system, which
  shows its own consent dialog and decides.

### Changed

- **The right-click menu is less thin.** *Copy as path* (on Windows' own
  `Ctrl+Shift+C`), *Duplicate*, *Rename in bulk* and *Open terminal here* are
  back at the top level. Consolidating the menu had pushed them behind *More*,
  which made it tidy and made the things people reach for a hover further away.

### Fixed

- **The settings, split and details buttons are on the rightmost pane again.**
  They were moved there deliberately and an audit put them back on both sides,
  reading the panel toggle's "for this side" tooltip as meaning the left half
  had lost the feature. It had not: F11 toggles whichever side is active.

## [0.7.1] — 2026-08-11

### Fixed

- **Clicking *New* in the right-click menu killed the application.** Also
  *More ▸ Scripts*, if you had any scripts. Consolidating the menu moved those
  entries underneath other entries, and a style written to give each item in a
  submenu its command turned out to reach the submenu itself as well — so the
  submenu ended up holding a command meant for one of its own items, which
  Avalonia calls the moment the parent opens. Nothing caught it: the markup
  compiles, the bindings resolve, and the submenus that were not moved go on
  working.

## [0.7.0] — 2026-08-11

### Added

- **The details panel can be resized.** Drag the edge between it and the
  listing. The width is remembered per side and comes back with your session.
- **Places can be removed.** Right-click one you added and choose *Remove from
  places*. Adding was reachable two ways and removing none, so the only way to
  drop a place was editing a file by hand.
- **Keyboard shortcuts appear in the right-click menu**, beside the entries that
  have them.
- **Handing a folder to the running copy works.** Opening a folder from
  elsewhere — as the default file manager, or `vaktari ~/Downloads` from a
  script — now opens a tab in the window you already have. It never once did.

### Changed

- **The right-click menu hides what does not apply.** Right-clicking empty space
  no longer offers Open, Copy, Cut, Rename or *Move to bin*, all of which were
  enabled and did nothing. Right-clicking a file still selects it first, so
  nothing disappears when you actually mean it.
- **Sharing is one entry** covering all three of its states, including while
  copyparty is installing — during which it used to vanish from the menu with
  nothing said.
- Settings calls the path bar the path bar, and the sorting checkbox is
  *Arrange (sort and group)*, matching the menu it governs.
- The README calls it *places* throughout, matching the interface, rather than
  alternating between places and pins.

### Fixed

- **The bin refuses operations that would destroy the wrong file.** A row in the
  bin carries the path the item came from, so deleting or renaming one acted on
  whatever occupies that path *now*. Trash `notes.txt`, write a new one, delete
  the bin row, and it was the new file that went. Restore and Empty are what the
  bin offers; the rest is refused and says so.
- **Pasting into the bin or a Recent listing** created a folder literally named
  `vaktari:trash` in the working directory, moved the files into it, deleted the
  originals and reported success. Only on Linux — Windows was saved by a colon
  being illegal in a path.
- **Uninstalling no longer leaves folders unopenable.** Registering as the
  default file manager writes a shell verb pointing at the executable, and
  removing the program left the verb behind, so every double-clicked folder
  failed with an error naming a missing file rather than the program that
  registered it.
- **The details panel's resize handle did nothing** — it painted a bar and set a
  resize cursor over a control that could not move anything.
- **Emptying the bin said nothing when it failed**, which was indistinguishable
  from an already-empty bin. Restoring counted only its successes, so a file
  that could not go back looked like one you had not selected.
- **"Show tooltips on rows" only silenced some tooltips**; the path tooltip
  ignored it.
- Scrolling a folder of pictures no longer reads image headers on the thread
  drawing the scroll, and the thumbnail cache is bounded by memory rather than
  by a count that meant anywhere between 40 MB and 2.4 GB depending on the
  layout you happened to be using.
- The sidebar no longer builds its list of places on the drawing thread at
  startup, where a disconnected network drive froze the window for as long as
  the network took to give up.
- Navigating away from a large repository no longer leaves a `git` process
  running behind it.
- Both panes show the details-panel toggle in split view. It was treated as a
  window-level control, so the left half had a panel and no way to open it.
- `BUILDING.md` no longer warns that a tracked file is untracked, and
  `brand/install.sh` no longer defaults to a path under a project directory from
  two renames ago.
- The Arch package supersedes the old one and ships the symbolic icon the RPM
  already did.
- The test suite ran two headless tests at once on a single UI thread, which
  made it fail twice for reasons unrelated to what it was testing.

## [0.6.1] — 2026-08-09

### Changed

- **Renamed from Heimdall to Vaktari.** The old name is widely used by other
  projects. Settings, tabs and folder views carry over; on Windows the installer
  replaces the old installation rather than leaving two copies, and hands the
  folder classes back if the old one had claimed them.

## [0.6.0] — 2026-08-09

### Added

- **Properties opens the Windows shell's own sheet**, with its tabs, its
  security page and its handlers, instead of an imitation.
- **Offer to open folders**: Vaktari can register as the program that opens
  folders and drives.
- **A light scheme**, and a *Follow the desktop / Light / Dark* choice in
  Settings.
- **Open with** lists the applications actually registered for the file type,
  with a way out to the system's own picker.
- The search field folds away into its icon when empty, and the path bar keeps
  the end of a long path rather than the beginning.
- The Windows installer stops before overwriting a running copy.

### Changed

- New file-type icons across seventeen categories; folders show a page when they
  have something in them; the sidebar icons and the two storage icons were
  redrawn as families.
- Denser rows, two typefaces and three columns, following the interface
  proposal. The column browser and tags were removed.
- The Recycle Bin is called the Recycle Bin on Windows.
- The window's own controls sit on one side in split view.

### Fixed

- Escape cancels the bin prompt.
- The whole row is clickable, not only the filename.

## [0.5.1] — 2026-08-06

### Fixed

- The design scheme overwrote a font chosen moments earlier in Settings.

## [0.5.0] — 2026-08-06

### Added

- **Network shares on Windows**: SMB, and WebDAV where the WebClient service is
  running. Discovery, connection and the credential prompt are the system's own.
- Search accepts globs, and Windows bookmarks kept as files are imported.

### Fixed

- Git for Windows is routinely installed without going on `PATH`; it is found
  anyway.
- A console window flashed on every folder listing.
- Several sidebar rows trimmed their labels wrongly or could not be opened once
  connected.

## [0.4.0] — 2026-08-05

### Added

- **Windows is a supported install**, packaged with Inno Setup, with an icon on
  the executable and a checksum on the download.
- The design reference's palette, typeface, icons and stroke weights, applied
  verbatim.
- Toolbar search and an inline filter.

### Fixed

- Breadcrumbs use the platform's own separator, and none after a root.
- Search walks off the drawing thread, and debounces.

## [0.3.1] — 2026-07-30

### Added

- `--version` prints the build and the file it is running from, which is how you
  tell a stale local install from a packaged one.

## [0.3.0] — 2026-07-30

### Added

- Rubber-band selection in every layout, including the list.
- Compact view is virtualized.

### Changed

- Settings that did nothing were removed, and silent failures now say something.

## [0.2.0] — 2026-07-29

### Added

- **Version-control marks in every layout**, refreshed as files change and as
  the repository does, with a settings toggle.
- The bin's action bar.

### Fixed

- *Go up* is disabled on listings that are views rather than folders.
- The desktop entry and the window's `WM_CLASS` match, so the panel shows the
  right icon.

## [0.1.2] — 2026-07-28

First tagged releases. Linux tarball and RPM.

[Unreleased]: https://github.com/dkflint723/vaktari/compare/v0.7.1...HEAD
[0.7.1]: https://github.com/dkflint723/vaktari/compare/v0.7.0...v0.7.1
[0.7.0]: https://github.com/dkflint723/vaktari/compare/v0.6.1...v0.7.0
[0.6.1]: https://github.com/dkflint723/vaktari/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/dkflint723/vaktari/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/dkflint723/vaktari/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/dkflint723/vaktari/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/dkflint723/vaktari/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/dkflint723/vaktari/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/dkflint723/vaktari/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/dkflint723/vaktari/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/dkflint723/vaktari/releases/tag/v0.1.2
