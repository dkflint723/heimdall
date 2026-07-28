# Heimdall — session handoff, 27 July 2026

Cross-platform file manager in C# / Avalonia 12.1 / .NET 10. Linux-first
(Fedora KDE), Windows deliberately last. Repo at `~/dev/rove` on
`fedora-desktop`. Read `HANDOFF.md`, `PARITY.md` and `DECISIONS.md` in the repo
first — they are authoritative for architecture, status and rationale
respectively.

---

## The one thing to get right immediately

**Hand over this exact block after every change.** `-m` on the tar is not
optional: without it, extracted files keep the archive's old mtimes, MSBuild
decides nothing changed, and you silently test a stale binary. This was
misdiagnosed for weeks once.

```bash
cd ~/dev/rove
git add -A && git commit -m "wip"
pkill -f Heimdall.Ui
tar -xmvf ~/Downloads/heimdall-full.tar.gz
git status --short
dotnet build && dotnet run --project src/Heimdall.Ui
```

`git status --short` after the extract proves files landed — git compares
content, not timestamps. If it is empty, the archive did not arrive (check for
`heimdall-full (1).tar.gz` in Downloads).

---

## IMMEDIATE NEXT TASK — grid keyboard navigation needs focus on an item

**Symptom (user's words):** "It makes me click on something before I can press
home or end. I can't just click into the explorer and press as needed."
**Details view does not have this problem** — it uses Avalonia's own
`VirtualizingStackPanel`.

**What is established:**

- `VirtualizingWrapPanel.GetControl` **is reached** when an item has focus. A
  temporary diagnostic prints:
  `[heimdall] nav: Last from=ListBoxItem current=4 count=100000 wrap=False`
- `First` and `Last` in that method ignore `current` entirely — they return index
  0 and count−1 — so **if the key arrives, it works regardless of selection.**
- Therefore the failure is almost certainly that **the key never reaches the
  panel when no item has focus**: clicking empty space below the tiles focuses
  the scroll area, and directional navigation starts from the focused element.

**Not yet confirmed:** nobody has captured the *negative* case — click empty
space, press End, and verify that **no** `nav:` line appears. Do that first. If a
line does appear, the above reasoning is wrong and the fault is in `GetControl`
or in what it returns.

**Likely fix, once confirmed:** make a click on empty listing space focus the
list (or its first/selected item), so navigation has an origin. That is a
window-level concern, not a panel one.

**A temporary diagnostic is still in the tree and must be removed when done:**
the `[heimdall] nav: …` line at the top of
`VirtualizingWrapPanel.GetControl`.

**Second, unverified symptom from the same report:** "sometimes it would open the
files" when pressing Home/End. This was *inferred* to be keys falling through to
other handlers when `GetControl` returned null, and that null is now fixed — but
**it was never diagnosed and never retested.** Ask whether it still happens
before assuming it is gone.

---

## The virtualizing wrap panel — built this session, working

`src/Heimdall.Ui/VirtualizingWrapPanel.cs`, ~340 lines. **This closed the last
real parity gap against Dolphin.**

**Measured, on a 100,000-item folder:**

| | before | after |
|---|---|---|
| grid, 20,000 items | 6,841 ms | — |
| grid, 100,000 items | refused | **20 ms, then 7 ms** |
| containers realized | all of them | **48, constant** |

Scrolling to the end gives `range=99952..99999 viewport=1631866..1632507` with
48 containers — the cost genuinely does not grow with the folder.

**How it was built, because the method mattered more than the code.** The
assistant has no SDK, no Avalonia assemblies and no network, so writing against a
remembered abstract API was the exact failure mode that had already cost several
rounds this session. Instead:

1. `strings` on the shipped `Avalonia.Controls.dll` proved **no wrapping
   virtualized panel exists** in 12.1 (only `VirtualizingStackPanel` and
   `VirtualizingCarouselPanel`).
2. A stub inheriting `VirtualizingPanel` and implementing nothing was shipped
   **so the build error would list the abstract members**.
3. `ilspycmd` (installed to `~/.dotnet/tools`) decompiled `VirtualizingPanel`
   and `ItemContainerGenerator` for the real signatures and the documented
   container lifecycle.

The only build errors in the real implementation were four CS0507s: a
`protected internal` member from another assembly must be overridden as plain
`protected`. Nothing about the protocol was wrong.

**Design facts worth keeping:**

- Uniform tiles are the whole trick — the row holding item N is arithmetic.
- `ItemSpacing` (default 6) is separate from `ItemWidth`/`ItemHeight` because the
  grid item template carries `Margin="3"`; the cell must be 6 px larger or every
  tile is uniformly clipped.
- Realization is driven by `EffectiveViewportChanged`, which the base class docs
  offer as the simpler alternative to `ILogicalScrollable`.
- Containers are recycled into a pool keyed by the generator's recycle key and
  **hidden rather than removed** — the generator's docs require this.
- `OnItemsChanged` is deliberately blunt (recycle all, rebuild). Index shuffling
  is where `VirtualizingStackPanel` spends much of its 1,278 lines and listings
  here reload wholesale.
- `avail` is reported as `995xInfinity` — a ScrollViewer measures with infinite
  height. The `_viewport.Height > 0` fallback handles it.

**Still to do on the panel:**

- **Compact is still the un-virtualized `WrapPanel`** and takes **32 seconds** on
  100,000 items. It keeps the item limit for that reason. It wraps into
  **columns**, so virtualizing it needs an orientation mode — the row arithmetic
  becomes column arithmetic and `GetControl` swaps its axes. Same shape of work.
- **`PageUp`/`PageDown` currently move one row**, which is wrong; a page should
  be a viewport's worth of rows.
- **Remove the `[heimdall] wrap: …` diagnostic** from `MeasureOverride`, or gate
  it behind an env var. It fires on every measure above 1,000 items.
- Not yet checked under hard scrolling: **stale icons on recycled tiles**, and
  **selection landing on the wrong file** after scrolling away and back. Those
  are the classic recycling faults and the container count cannot reveal them.

**The guard is now split** (`PaneViewModel`): `CanUseGrid` is always true,
`CanUseCompact` keeps the 5,000 limit, and the drop-back-to-list only fires for
compact. `HEIMDALL_TILE_LIMIT` still overrides the limit and is now a foot-gun
for compact specifically.

---

## Everything else finished this session

- **Settings dialog** — all six pages built and working. Page selector reworked
  to a left strip styled like the sidebar, own `TabItem` template (Fluent's moved
  the label between states), 700×560 with per-page scrolling, Dolphin's page
  order.
- **Trash sweep fully verified on real files** — age expiry, the
  unparseable-date safety rule, the size arithmetic, and both delete actions,
  including that it stops at the allowance rather than emptying. Found and fixed
  `Allowance` measuring the wrong volume (`Path.GetPathRoot` returns `/` for
  every path on Linux).
- **`xdg-mime` per-row subprocess** exhausting the thread pool — a 44-second
  navigation. Now cached per path and capped at 4 concurrent, try-acquire.
- **Type-ahead** built (was in the v1 scope and never implemented).
- **Tab completion** fixed — it concatenated instead of cycling.
- **Double-click activation** — two clicks on the same row open it, no time
  window. A product decision, not a timed OS double-click.
- **Tag removal** via item menu and sidebar right-click; `ITagStore.ForgetKnown`.
- **New folder now renames immediately; New file added** with eight built-in
  kinds.
- **Breadcrumb ate underscores** — `Button Content=` parses `_` as an access key.
  Filenames are data and must never reach `Content=` or `MenuItem Header=`.
- **Six disagreeing byte formatters** collapsed into `Core.FileSystem.ByteSize`.
- **Sidebar** — folders tree removed, tags moved down, frequent-folders list
  added (`IVisitStore`, plain counts, recorded only on user-initiated
  navigation).
- **NativeAOT publish verified** clean.
- **Backup**: the only git remote was a stale local bundle on the same disk. Now
  bundled to `/mnt/steam-games/git_projects/heimdall/`. **Still no genuinely
  off-machine copy.**

---

## Working practices that were learned the hard way

**Instrument before theorising.** This session had a six-wrong-theories hunt that
a stack dump ended in one step, and a four-round hunt for a binding bug that did
not exist. Every piece of real progress came from a measurement.

**Ask what else explains a single data point.** The four wasted rounds rested on
one ambiguous observation that was equally explained by a missed click.

**Two of the six wrong theories found real bugs** — which is exactly what made
them convincing. A true finding is not automatically the relevant one.

**Check a checker before trusting it.** Two verification scripts lied this
session: a `[RelayCommand]` regex reported 31 false failures, and an `IsVisible`
regex silently never matched, "confirming" both presence and absence of the same
binding. `grep` settled both.

**An XML parse says well-formed, not correct.** A line filter once orphaned
attribute fragments mid-file and `ET.parse` accepted it.

**A diagnostic that omits the deciding field is barely better than none.** The
trash sweep logged only when it deleted, so silence meant three different things.

**Never remove a diagnostic before the user confirms the fix.** Doing so once led
to shipping a fix that did not work.

**When a framework template misbehaves and you cannot read it, supply your own**
rather than guessing at setters to neutralise.

---

## Environment notes

- No .NET SDK in the assistant's container. Static checks only; the user's build
  is the verdict. Parse XAML as XML and cross-check every `*Command` against a
  real `[RelayCommand]` before shipping.
- `ilspycmd` is installed on the user's machine at `~/.dotnet/tools` — decompile
  rather than recall Avalonia APIs.
- Debug env vars: `HEIMDALL_TILE_LIMIT`, `HEIMDALL_LOAD_DEBUG`,
  `HEIMDALL_ICON_DEBUG`, `HEIMDALL_FONT_DEBUG`.
- Test folders: build them in `~`, never `/tmp` (different filesystem — trashing
  copies instead of renaming), and **give files extensions** or every row misses
  the mime glob path and spawns a subprocess.

---

## Remaining backlog after the immediate task

1. Virtualize compact (orientation mode on the panel).
2. Remove the temporary `nav:` and `wrap:` diagnostics.
3. Hard-scroll grid at 100k checking for stale icons and selection drift.
4. `DECISIONS.md` addendum was written and appended; one bullet in it is now
   stale (it says type-ahead was never built).
5. Deferred settings, each waiting on its feature: VCS decorations, selection
   mode, spring-loaded folders, expandable folders, multi-window, rename dialog,
   configurable shortcuts.
6. An off-machine backup.
7. Windows port — deliberately last, do not start unless asked.
