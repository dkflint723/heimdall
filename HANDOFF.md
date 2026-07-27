# Heimdall — handoff

A file manager for Fedora KDE and Windows, built to be daily-driven instead of
Dolphin and Explorer. This document is what you read to pick the project up
cold.

**Where things are written down, and why it matters.** This file drifted badly
once because feature status was recorded in two places and only one of them ever
got updated. The split is now strict:

| Document | Carries | Does not carry |
|---|---|---|
| `HANDOFF.md` (this) | what the project is, how it is built, what will bite you | per-feature status |
| `PARITY.md` | the Dolphin gap list and the plan — **the only authoritative status** | architecture, practices |
| `DECISIONS.md` | rationale behind individual choices | status, architecture |

If you want to know whether something is built, `PARITY.md` is the answer and
this file is not. Do not add a feature checklist here.

---

## 1. What this is, and what it is not

**Goal:** a lightweight, powerful file manager that feels at home on both KDE
and Windows, used as a daily driver by its author and a few friends. Not
commercial, not a shell replacement.

**The problems it exists to solve**, in the author's words: Explorer is slow on
huge and network folders, has no real tabs or dual-pane or keyboard navigation,
useless search, and is bad at SMB and remote shares. OneCommander was closer but
forgets open folders on restart and feels clunky.

**Binding constraint — accessibility.** This is a requirement, not polish:

- Text must be resizable. Fonts and icons scale on **independent** axes.
- The UI must work for colourblind users. **Hue never carries meaning on its
  own.** Selection has an edge bar as well as a fill; tags carry their name
  beside the swatch; file age is a lightness ramp, never a red-to-green heat
  map.

That rule paid for itself: because nothing depended on a particular hue, the
entire palette could later be handed over to the desktop's colour scheme without
weakening anything.

**Non-goals:** shell replacement, mobile, plugin marketplace, cloud sync.

---

## 2. Build and run

```bash
cd ~/dev/rove          # directory still carries the pre-rename name
dotnet build && dotnet run --project src/Heimdall.Ui

# Release, ahead-of-time, fully trimmed — the shape that actually ships
dotnet publish src/Heimdall.Ui -r linux-x64 -c Release /p:PublishAot=true
```

Fedora's own .NET 10 SDK, Avalonia 12.1. `Directory.Build.props` enables the
trim and AOT analysers in every project, so trim-hostile code fails the build
rather than surfacing months later in a published binary.

Diagnostics are on stderr and prefixed `[heimdall]`:

```bash
dotnet run --project src/Heimdall.Ui 2>&1 | grep -a heimdall
HEIMDALL_ICON_DEBUG=1 dotnet run --project src/Heimdall.Ui 2>&1 | grep -a icon
```

`HEIMDALL_ICON_DEBUG` dumps per-shape bounds and paint for every icon rendered.
It was `ROVE_ICON_DEBUG` before the rename; this document told you to use the old
name for a while, and setting it silently did nothing.

State lives in `~/.local/state/heimdall/` — `session.json` (+ `.bak`),
`places.json`, `tags.json`, `instance.lock`. Scripts live in
`$XDG_DATA_HOME/heimdall/scripts` and are passed both `HEIMDALL_CWD` /
`HEIMDALL_SELECTED` and the old `ROVE_` names, because user scripts live outside
this repo and a rename must not break them.

**Two files the build needs are not in the repo.** `src/Heimdall.Ui/app.manifest`
(referenced by `<ApplicationManifest>`) and `src/Heimdall.Ui/heimdall.png`
(referenced by `<AvaloniaResource>`, loaded by `AppIcon` through
`avares://Heimdall.Ui/heimdall.png`). A fresh clone into an empty directory fails
on the first and loses its window icon on the second. Check `.gitignore` before
assuming they are safe.

---

## 3. Architecture

```
Heimdall.Core     platform-agnostic. Never references InteropServices.
Heimdall.Linux    [assembly: SupportedOSPlatform("linux")]
Heimdall.Ui       Avalonia. Depends on Core; names a platform type in ONE place.
Heimdall.Windows  not started, and deliberately last
```

**The platform seam is a single object.** `IPlatform` in Core bundles every
OS-specific provider — filesystem, operations, launcher, places, search,
thumbnails, metadata, properties, access editor, scripts, tags, theme, icons,
sharing, remote mounts, network discovery. `LinuxPlatform` is the Linux
composition root. `MainWindow` constructs it inside one
`OperatingSystem.IsLinux()` check and never mentions a platform type again.

For the Windows port: add `WindowsPlatform`, make the `Heimdall.Windows`
ProjectReference conditional, and put `#if` around that single construction. The
UI needs no other change — which is the theory, and it is still only a theory.
There are twenty interfaces in Core and each has exactly one implementation. The
second implementation is where you find out which of those shapes were really
about Linux, and that remains the largest unvalidated assumption in the project.

`Heimdall.Linux` is annotated Linux-only at assembly level rather than by target
framework — **there is no `net10.0-linux` TFM**; .NET only defines OS-specific
frameworks for windows, android, ios, macos, maccatalyst, tvos and browser. The
annotation removes every per-call platform guard inside the project and pushes
the requirement onto callers, which is what forced the single-seam design. It
immediately caught a real leak: the shell had been casting to a concrete Linux
type to reach an event, now on the Core interface where it belongs.

### Patterns that recur

**Per-row work goes through attached properties on the realized control.**
Thumbnails, themed icons, inline metadata, permissions and tags all attach to the
control the list virtualization actually creates, so only visible rows pay.
`FileEntry` must stay stat-free — never widen it to carry per-row data, or
enumeration stops being fast.

**Streaming enumeration.** `System.IO.Enumeration.FileSystemEnumerable`, channel
batched, flushed to the dispatcher on a 100 ms timer. A 200,000-entry flat
directory paints its first rows in **3 ms** and completes in about 3.4 s.

**Generation counters, not just cancellation tokens.** State captured before an
`await` cannot be trusted after it. Any async handler that mutates a bound
collection re-checks a generation counter *inside* the dispatcher block. An
`async void` handler needs a catch-all, or an exception becomes a process abort.

**Derived metrics.** Every size is a `DynamicResource` computed from the font and
icon scales. Row height is `max(body × 2.1, thumb + 8)` — it cannot be a free
setting, because a row must fit the taller of its label and its icon.

**Preferences are a separate store, read first.** `settings.json` sits beside
`session.json` in `~/.local/state/heimdall/`, with its own source-generated
context and version. The session records *where you were*; settings record *what
you always want*, and they collide — "restore my last folders" and "always start
in Home" are both startup settings and one has to win. So the constructor order
is **settings → theme → session**, and that ordering is load-bearing rather than
tidy: `ThemeApplier` reads `AppSettings.Current` to decide whether a configured
font beats Plasma's, so loading settings after it meant the font setting did
nothing at all, even across a restart.

**A setting that lands in a theme resource needs `ThemeApplier` re-run on save.**
The resource is the only thing the markup reads, and Apply had exactly two call
sites — startup and a Plasma scheme change — so saving changed nothing.

**Prefer the desktop's own data over private equivalents.** XDG trash, the
freedesktop thumbnail cache, `user.xdg.tags`, `kdeglobals`, shared-mime-info, the
icon theme spec, gvfs and kio-fuse mount points. Everything Heimdall writes, the
rest of the desktop can read. This is the same instinct behind consuming existing
network machinery rather than reimplementing protocols.

---

## 4. The shape of the application

Not a status list — `PARITY.md` has that. This is the vocabulary the code
assumes you already have.

A **window** holds a sidebar and one or two **pane groups** (split view, F3).
Each pane group has its own tab strip, its own details panel, and an active
**pane**. A pane is one directory and owns its view mode, sort, grouping, filter,
scale and history. `PaneGroupViewModel` owns anything that is per split side —
the details panel lives there rather than on the window, because a single shared
panel swapped content as focus moved between halves, which defeats the entire
point of comparing two folders.

Three view modes — `ViewMode.Details | Grid | Compact` — plus an optional Miller
column strip (F8) above any of them. **They are three separate `ListBox`es that
all stay alive when hidden**, by `IsVisible` rather than by unloading. That
single fact is behind the two worst bugs in the project's history; both are in
§5.

Sidebar sections: search, places, tags, devices, network (discovered), remote
(mounted), and a collapsible folder tree, all visible at once.

The status bar carries two independent things that must not be conflated:
`Summary` is the item and selection count, `Status` is for messages. They once
both printed the count, which read as "36 items   36 items".

---

## 5. Constraints that will bite again

These all cost real time. None are hypothetical.

### The three-layouts problem

Details, grid and compact are three live `ListBox`es. Two consequences, both
already paid for:

- **Selection.** All three had `SelectedItems` bound to *one shared collection*,
  so each wrote its own idea of the selection into it and a single click produced
  the union of whatever the other two still held — three different files selected
  from one click, quietly inflating the status bar and handing file operations
  the wrong set ever since the second layout was added. Deduplicating does **not**
  fix it, because the entries genuinely differ. The fix is **one selection
  collection per layout**, with `SelectedEntries` a computed property returning
  whichever layout is on screen and `CarrySelection` copying across on switch.
- **Realization.** Binding all three to `Entries` realized a container per file in
  two *invisible* layouts on every folder open — exactly the cost the streaming
  enumerator exists to avoid. `DetailsEntries` / `GridEntries` / `CompactEntries`
  return `Entries` only while that layout is on screen, and an empty array
  otherwise.

**In `OnViewChanged`, notify the entries properties BEFORE `CarrySelection`.** A
ListBox cannot hold a selection for items it does not yet have.

### Avalonia has no virtualizing wrap panel

Grid and compact use `WrapPanel`, so every item they are given is realized. Above
`UnvirtualizedLimit = 5000` the tile layouts are refused: their buttons disable,
the menu explains why, and navigating into a huge folder while already in one
drops back to list view. **Refusing, not truncating** — a file manager that
silently omits files is dangerous. This is a real parity gap tracked in
`PARITY.md`, not a settled design.

### Avalonia and XAML

- **Compiled bindings are on** (Avalonia 12 default, stated explicitly because
  AOT requires them). Every `DataTemplate` needs `x:DataType`, and **`Style`
  setters need it on the `Style` element** or they resolve against the window's
  type. Bindings inside a `ContextMenu` are built lazily — a missing command
  compiles and throws only when the menu first opens.
- **Never declare a runtime-written resource in markup.** `MainWindow.axaml`
  declared `FontSizeBase` in `<Window.Resources>`, which shadowed the scaled value
  written to `Application.Resources`, because a `DynamicResource` resolves
  nearest-outward. Text scaling silently did nothing. It fails by doing nothing
  rather than by erroring, which is why it lasted so long.
- **`ToggleButton`, not `RadioButton`, for icon-only choices.** Fluent draws a
  RadioButton's selection circle *beside* its content, so three layout buttons
  rendered as six glyphs in no obvious order. They still behave as one choice
  because `IsChecked` is OneWay-bound to the view mode. In the flyout, where each
  option carries a text label, `RadioButton` is correct and stays.
- **Never insert into `MainWindow.axaml` by matching a binding string.** The
  Miller column template binds `{Binding Entries}` exactly like the pane's list.
  Locate the enclosing `x:DataType` first.
- Toolbar buttons are `DockPanel.Dock="Right"`, so **declaration order is the
  reverse of screen order**. Left to right the intent is: list, small grid, large
  grid, │ rule, details panel.
- **`DrawingGroup.ClipGeometry` does not affect `GetBounds()`** in Avalonia (issue
  #18512, deliberately unlike WPF), and `DrawingImage.Size` is exactly
  `Drawing.GetBounds().Size`. A clip can never correct an image's size.
- **Avalonia objects must be constructed on the UI thread.** Building a
  `DrawingImage` or reading `Application.Current.Resources` from a pool thread
  crashed the process. `Bitmap` is a plain object and is the exception.
- **`DoDragDropAsync` takes `PointerPressedEventArgs` specifically.** Holding the
  press args until a movement threshold is crossed looks wrong but is forced by
  the API. Do not "fix" it; the attempt does not compile.
- **Avalonia 12 clipboard and drag-drop** use `DataTransfer` / `DataTransferItem`.
  Do not declare your own `text/uri-list` — it collides with `DataFormat.File` and
  the transfer is discarded. `FlushAsync()` on X11. Cut across applications needs
  `application/x-kde-cutselection=1`. Handle `DragEnter` as well as `DragOver`.
- **Avalonia's folder picker is unusable from a modal window on Linux**
  (AvaloniaUI/Avalonia#10998 returns null, #6589 hangs). The share dialog browses
  with Heimdall's own directory listing — the right instinct for a file manager
  anyway.
- **TreeDataGrid is rejected** — it needs an Avalonia Accelerate licence and was
  the wrong control regardless. `ListBox` + `TreeView`.
- **`TextBox.Watermark` is obsolete in Avalonia 12** — use `PlaceholderText`.
  More useful than the rename: **`TreatWarningsAsErrors` does not catch Avalonia
  XAML compiler warnings** (`AVLN####`). They compile straight through and appear
  only in the build log, so read the log rather than the exit code.
- **`DynamicResource` assigns without converting.** Every metric in
  `PaneScale.Compute` is a `double`; a resource read back into an `int` property
  such as `MaxLines` fails at runtime, not at compile time. That is why the
  icon-view text settings are not wired.
- **Grep where an attached property is actually attached before assuming what it
  governs.** `DoubleClick.Command` sounds like the open-a-file path and is used
  on exactly one control — the path bar's edit layer. Opening files is a
  window-level handler.
- **One formatter per fact.** Six private byte formatters had accumulated and
  they disagreed: 500 bytes read as "500 B" in properties and "0.5 KB" in the
  sidebar, and only the Size column used binary unit names for 1024-based maths.
  `Core.FileSystem.ByteSize` is now the only one.

### Async and the dispatcher

- **A `_generation` counter is worthless unless it is COMPARED.**
  `LoadListingAsync` incremented one and never read it in any of its four
  dispatcher blocks. Cancelling a token does **not** unqueue a
  `Dispatcher.UIThread.InvokeAsync` callback already in flight, so a superseded
  enumeration appended into a list the newer navigation had just cleared — and
  worse, its completion block would point the file watcher at the old path and
  clear `IsLoading` for a live navigation. When a file declares `_generation`,
  grep that it is compared, not just incremented.
- **The useful audit question is not "is this block guarded" but "what does it
  mutate".** Fourteen unguarded blocks that only write `Status` are fine —
  guarding them would suppress genuine error messages. The check that matters:
  no dispatcher block touching `Entries`, `_all`, the watcher or `IsLoaded` may
  be unguarded.
- **Navigating to the folder you are already in is a no-op.** It used to reload,
  and because entries paint in readdir order and sort only when enumeration
  finishes, the rebuild flashed the same files in filesystem order before they
  settled. Refreshing on purpose is F5's job.

### Platform and process

- **X11 clipboard is owned by a live process.** Copy, quit, paste is not
  achievable. Do not chase it.
- .NET's named `Mutex` does not work for single-instance on Fedora. Use an
  exclusive `FileStream` lock.
- **`/proc/mounts` needs filtering** — exclude loop, zram and squashfs or snap
  mounts appear as drives named after revision numbers; dedupe by device or btrfs
  subvolumes appear repeatedly.
- Places must dedupe imported Dolphin and GTK bookmarks against XDG user dirs, or
  every standard folder appears twice.
- **A destructive action must never depend on a single hand-rolled key path.**
  Shift+Delete's Enter did nothing for three rounds; fixed by giving the prompt
  real buttons and focusing one.
- **`pgrep` before debugging anything backed by a shared file.** A "tabs
  duplicating" bug was two `dotnet run` instances.
- Tags live in `user.xdg.tags` on the files themselves, so moving documents needs
  `rsync -aX` or every tag is silently lost.
- `xdg-mime` is a shell script that spawns processes; `SharedMimeInfo` parses
  `/usr/share/mime/globs2` directly, longest suffix first so `.tar.gz` beats
  `.gz`. Keep the process as a fallback only. On Fedora KDE `xdg-mime default`
  fails outright with `qtpaths: command not found` — write
  `~/.config/mimeapps.list` instead.

---

## 6. Working practices

- **Ship a full `src` snapshot, not incremental patches**, and always extract with
  **`tar -xmvf`**. Without `-m`, tar restores archive mtimes older than the last
  build output, MSBuild concludes nothing changed, reports success in a tenth of a
  second having done nothing, and links a stale assembly. This was misdiagnosed
  for most of the project's life as "tarballs don't extract properly".
  `git status --short` settles it, because git compares content rather than mtime.
- **Verify before building.** `grep -c` for a symbol you just added is faster than
  a build cycle and catches a file that did not land.
- **Static-check XAML before shipping it.** Parse every `.axaml` as XML, confirm
  each `DataTemplate` and binding-bearing `Style` carries `x:DataType`, and
  cross-check every `*Command` binding against a real `[RelayCommand]`. Positional
  `ICommand` parameters on records — `TagOption.Command`, `PathSegment.Open` — are
  legitimate, and a naive checker will flag them.
- **Look at what surrounds an anchor before replacing a block.** Four build breaks
  in one session came from this: an insertion landed between a `[RelayCommand]`
  and its method, orphaning the attribute onto a field; a duplicate member was
  added beside one that already existed; a `case` was appended to a switch that
  already had it. Every one was visible in output already on screen.
  `grep -n -B4 -A4` first.
- **Instrument before theorising.** Repeatedly, plausible theories were wrong and
  one diagnostic settled it immediately. The worst case: a frozen download dialog
  was blamed on the default handler, then a hung mount, then our icons — all three
  wrong. A coredump backtrace showed `xdg-desktop-portal-kde` aborting inside its
  own file-dialog preview generator, a KDE bug unrelated to this project. When
  something is *invisible* rather than incorrect, add the trace first. And never
  suppress stderr on a diagnostic command; a `systemctl --user restart` with
  `2>/dev/null` hid the fact that the unit did not exist.
- **Read the evidence before shipping the fix.** A screenshot disproved a
  selection-bug theory that had already been half-built.

---

## 7. Open, and needing a decision

1. **The virtualizing wrap panel, or the 5,000-item guard.** Closing the gap means
   either a custom `VirtualizingPanel` that wraps — preserving ListBox selection
   and keyboard navigation, but hard to get right — or chunking items into rows and
   virtualizing the rows, which is easier but breaks ListBox selection semantics.
   Worth answering first: does the guard actually bite in daily use, or only in
   benchmarks?
2. **One icon still renders small.** Tela's `application-x-compressed-tar.svg`
   declares a 16×16 viewBox and paints 48×56. `HEIMDALL_ICON_DEBUG=1` dumps each
   shape's bounds and paint; the hypothesis is a shape spanning the full area
   without being visible, inflating the bounds.
3. **Windows is deliberately last** and was deferred explicitly. Do not start it
   unless asked.

Everything else planned lives in `PARITY.md`.
