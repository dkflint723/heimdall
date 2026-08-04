# Starting the Windows port

Written 29 July 2026, from the tree at that date. Everything below was read off
the source rather than recalled; where something is a guess it says so.

**This is a work plan, not a status document.** Delete it when the port lands.

---

## 1. The good news: there is exactly one seam

`MainWindow.axaml.cs` line ~54 is, in its own words, *"the one and only place a
platform type is named"*:

```csharp
IPlatform platform;

if (OperatingSystem.IsLinux())
    platform = new LinuxPlatform(JsonSessionStore.DefaultDirectory());
else
    throw new PlatformNotSupportedException(
        "No platform implementation for this operating system yet.");
```

Adding Windows means adding an `else if (OperatingSystem.IsWindows())` branch
and one project. **The UI names no other platform type** — verified by grepping
`Heimdall.Ui` for every `Xdg*`, `Linux*`, `Avahi*`, `Copyparty*` identifier; the
only hits are that one line, a comment, and a local helper misleadingly named
`XdgDeduplicate` (it implements the *freedesktop* "file (1)" rename convention,
which is not Windows' "file - Copy", so it needs a platform hook or a rename).

`IPlatform` is the whole surface: 19 members, seven of them nullable because the
interface already anticipates a platform that lacks the capability. **Those
nullables are the porting budget** — `Sharing`, `Remotes`, `Discovery`, `Theme`,
`Icons`, `TrashMaintenance`, `AccessEditor` can all return `null` on day one and
the application still runs.

---

## 2. Two blockers before any code — DONE

Both cleared 3 August 2026. **The property-function `Condition` syntax works** —
that was the open question, and it is now answered on a real Windows machine.

The reference is not conditioned on the OS directly, but on a `HeimdallPlatform`
property that *defaults* from the OS:

```xml
<PropertyGroup>
  <HeimdallPlatform Condition="'$(HeimdallPlatform)' == '' AND '$([System.OperatingSystem]::IsLinux())' == 'true'">Linux</HeimdallPlatform>
  <HeimdallPlatform Condition="'$(HeimdallPlatform)' == '' AND '$([System.OperatingSystem]::IsWindows())' == 'true'">Windows</HeimdallPlatform>
</PropertyGroup>
```

**The indirection is the point.** §8 warns that conditional references make it
possible to break the Linux build from a Windows machine without noticing, and
CI is a slow way to be told. With the override, either configuration compiles
from either machine:

```bash
dotnet build src/Heimdall.Ui -p:HeimdallPlatform=Linux
```

Only runtime behaviour still needs the other OS. Both configurations, and the
neither-selected case, were built on Windows before this was written.

`MainWindow.axaml.cs` is fenced with `HEIMDALL_LINUX` / `HEIMDALL_WINDOWS`,
defined beside the reference they belong to, and a `#else` arm carries an
`#error` naming the fix. **Give each arm its own `else`** — sharing one after the
`#endif` compiles, but leaves the `#error` arm ending in a dangling `else`, and
the five cascading syntax errors bury the message that explains the problem.

`[assembly: SupportedOSPlatform("windows")]` mirrors the Linux one.
**`Heimdall.Windows` stays on plain `net10.0`**, so it still compiles on the
Linux CI runner and is checked on every push — see §9.

**`Heimdall.Linux/AssemblyInfo.cs` carries `[assembly: SupportedOSPlatform("linux")]`.**
`Heimdall.Windows` needs the mirror image, and it can additionally use the real
`net10.0-windows` TFM, which Linux cannot (no `net10.0-linux` exists). That TFM
unlocks the Windows Forms/WPF interop surface if it is ever wanted — probably it
is not, but it also silences the platform analyser properly.

---

## 3. What is already portable, and must not be re-implemented

`Heimdall.Core` holds real logic, not just contracts. **None of this needs
touching:**

| | |
|---|---|
| `Vcs/GitVersionControl` | drives the `git` binary; behaves identically on Windows |
| `FileSystem/Checksums` | pure |
| `FileSystem/ImageSize` | header parsing, byte-level |
| `FileSystem/ByteSize`, `Grouping`, `FileKinds` | pure |
| `NaturalOrder` | pure |
| `PathCompleter` | **uses `/` in a doc comment only** — check the logic uses `Path.DirectorySeparatorChar` |
| `BatchRename` | pure |
| `PreviousName` | pure |

The whole `Heimdall.Ui` layer is Avalonia and portable in principle. Its problems
are path assumptions, not APIs — see §5.

---

## 4. The providers, ordered by how much they will hurt

### Trivial — a day, mostly BCL
- **`IFileSystemProvider`** — `Directory.EnumerateFileSystemEntries` already.
  Windows adds drive roots (`C:\`) where Linux has one `/`.
- **`IFileOperations`** — copy/move/delete are BCL. **Trash is the exception,
  see below.**
- **`IApplicationLauncher`** — `ShellExecute` via `Process.Start` with
  `UseShellExecute = true`. Easier than the Linux `.desktop` parsing.
- **`ISearchProvider`** — a directory walk. The Linux one shells out to `find`;
  a managed walk is fine and more portable.
- **`ITemplateProvider`** — Windows has `%APPDATA%\Microsoft\Windows\Templates`.
- **`IScriptRunner`** — same shape, different interpreter conventions.

### Moderate — real work but well-trodden
- **`IPlacesProvider`** — `SHGetKnownFolderPath` for Desktop/Documents/etc, plus
  `DriveInfo.GetDrives()`. **Registry-free via `Environment.GetFolderPath`,
  which covers most of it without P/Invoke.**
- **`IThemeProvider`** — registry: `HKCU\...\Themes\Personalize\AppsUseLightTheme`
  for dark mode, and DWM's `ColorizationColor` for the accent. Straightforward
  reads; no COM.
- **`IPropertiesProvider` / `IFileMetadataProvider`** — `FileInfo` covers most.
  Rich metadata (image dimensions, media duration) has no BCL equivalent —
  **`ImageSize` in Core already solves the image case.**
- **`IThumbnailProvider`** — Windows has `IShellItemImageFactory` (COM). A
  cheaper first pass: decode images directly with `ImageSize` + Avalonia, and
  return null for everything else. **The freedesktop thumbnail cache does not
  exist on Windows, so `XdgThumbnailProvider`'s whole caching strategy is moot.**

### Hard — expect these to dominate the schedule
- **Trash / Recycle Bin.** There is **no BCL API.** The options are
  `SHFileOperation` (ANSI/Unicode struct marshalling, deprecated but simple) or
  `IFileOperation` (COM, the supported route). **Both are P/Invoke or COM under
  NativeAOT, which is the risky combination** — COM in particular needs
  source-generated interop or it will fail at runtime, not compile time. Budget
  real time here, and see §6.
  **`ITrashMaintenance` also needs `List`/`Restore`/`Empty`** — the Recycle Bin
  exposes these through the same COM surface. Returning `null` for
  `TrashMaintenance` on day one is legitimate and skips all of it.
- **`ITagStore`.** Linux uses **xattrs**, which Windows does not have. The
  nearest equivalent is **NTFS Alternate Data Streams** (`file.txt:tags`), which
  are invisible, survive copies within NTFS, and are **silently destroyed by any
  copy to FAT/exFAT or by most archivers.** The honest alternative is a sidecar
  store keyed by path, which then goes stale on rename. **This is a design
  decision, not an implementation detail — decide before writing code.**
- **`IIconThemeProvider`.** The whole model differs. Linux has a theme of named
  icons the app resolves; Windows has **per-file icons** from
  `SHGetFileInfo`/`IShellItemImageFactory`. `IconLoader`'s SVG renderer and
  `XdgIconTheme`'s name resolution have **no Windows counterpart at all.**
  Returning `null` for `Icons` falls back to the drawn glyphs in
  `IconLoader.Fallback` and `SidebarIcon` — **which is why those exist and are
  hand-drawn rather than themed.** Start there.
- **`IAccessEditor`.** POSIX modes have no meaning on Windows; ACLs are a
  different model entirely. **Return `null`** — the interface is already nullable
  for exactly this reason.

### Not worth doing
- **`INetworkDiscovery`** — Avahi has no Windows equivalent worth the effort.
  Return `null`.
- **`IRemoteMounts`** — `gio` has no counterpart; Windows mapped drives appear as
  ordinary drive letters through `IPlacesProvider` anyway. Return `null`.
- **`IFileSharing`** — copyparty runs on Windows if Python is installed; the
  existing `CopypartyShare` logic is mostly path handling and could move to Core.

---

## 5. The path assumptions — DONE

**All fifteen POSIX assumptions in `Heimdall.Ui` now route through
`Heimdall.Core.FileSystem.PathRules`** (31 July 2026): `IsRoot`, `Normalise`,
`Parent`, `LeafName`, `Same`, `Ancestors`. Pure string shape — it never touches
the filesystem, so anything needing the disk stays on `IFileSystemProvider`.

**Linux behaviour is unchanged**, verified by porting both the new rules and the
inline code they replaced to Python and comparing case by case. The one
deliberate difference: `"//"` used to normalise to `""` — an empty path, a latent
bug — and now gives `/`.

What the port gets for free:

- **`IsRoot` asks `Path.GetPathRoot`** rather than comparing to `"/"`. A path
  equal to its own root IS the root, so `C:\` and UNC share roots work with no
  platform check.
- **A root keeps its trailing separator.** Trimming `/` leaves `""`; trimming
  `C:\` leaves `C:`, which on Windows means "the current directory on drive C" —
  a different place.
- **`Parent` returns null at a root, not empty.** `Path.GetDirectoryName` returns
  an empty string for a bare name, which had already caused a live bug where the
  Up button enabled itself on a virtual path and then did nothing.
- **`Same` compares `OrdinalIgnoreCase` on Windows and `Ordinal` on Linux**, so
  place highlighting and duplicate-tab detection are right on both: two paths
  differing only in case are one folder on NTFS and two on ext4.
- **`Ancestors`** replaced the column-strip walk that would not have terminated.

Two things deliberately left alone:

- **`FileClipboard`'s `file://` conversion still splits on `/`.** A URI is not a
  path — RFC 8089 uses `/` on every platform — and Windows exchanges files as
  **`CF_HDROP`**, so this needs a different mechanism rather than a separator fix.
  Annotated in place so a sweep does not "correct" it.
- **`VirtualPaths` keeps its `heimdall:` prefixes.** The old rationale (real paths
  start with `/`) stops being true on Windows, but `heimdall:` still cannot
  collide with `C:\`.

### 5a. One thing §5 got wrong: `PathRules` is separator-sensitive

Found 3 August 2026, running `PathRules` on Windows for the first time. **The
rules handle real Windows paths correctly** — `IsRoot(@"C:\")`, UNC share roots,
`Parent`, `LeafName` and `Ancestors` all behave — but **`Normalise` never
unifies the separator character**, and on Windows both `\` and `/` are legal:

```
Same(@"C:\Users", @"C:/Users")        = False   <-- one folder, two spellings
Same(@"C:\Users", @"C:\Users\")       = True
Same(@"C:\Users", @"c:\users")        = True
Ancestors(@"C:/Users/flint")          = ["C:\", "C:\Users", "C:/Users/flint"]
```

`Same` handles the trailing separator and the case rules — the two things §5
went looking for — and misses the third. **This is not theoretical:** Windows
accepts `C:/Users` everywhere, so it is what a paste into `Ctrl+L` can produce,
and `Same` is exactly what drives **place highlighting and duplicate-tab
detection**. Typing a path with forward slashes opens a second tab on a folder
already open and leaves the sidebar entry unhighlighted.

The `Ancestors` result is the same fault seen from the other side: the last
element is the normalised input and keeps its `/`, while every ancestor comes
from `Path.GetDirectoryName` and gets `\`. **One list, two conventions**, which
the column strip then compares with `Same`.

**The fix is one line in `Normalise`**, and it is a no-op on Linux, where
`AltDirectorySeparatorChar` and `DirectorySeparatorChar` are both `/`:

```csharp
path = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
```

Do this before `IFileSystemProvider`, not after — every path comparison in the
port stands on it. `\` is a legal *filename* character on Linux, which is why
this must go through the platform's own separator constants rather than a
literal.

### 5b. The `PathRules` tests are Linux-only, and say otherwise

`tests/Heimdall.Core.Tests/PathRulesTests.cs` opens by claiming "everything here
runs on any platform: the assertions are about POSIX paths, which the rules must
handle identically wherever they execute." **That is false, and 7 of 56 tests
fail on Windows** — `IsRoot("/")`, `Parent("/home")` and both `Ancestors` cases.

They are not finding the bug in 5a. They fail because a POSIX literal means
something different here: `/` is "the root of the current drive", so
`Path.GetPathRoot("/")` answers `\` and the string comparison in `IsRoot` says
no. The assertions encode Linux, which is what they were for.

**Decide before step 3**, because until then a Windows dev loop has no green
baseline and every real failure has to be picked out of seven expected ones.
Either make the expectations platform-conditional and add the `C:\` cases beside
them, or split the POSIX assertions into a Linux-only fixture and write a
Windows one. The second is more honest about what is being asserted.

---

## 6. NativeAOT is the constraint that will surprise you

The project publishes with `PublishAot=true` and `TrimMode=full`, and
`Directory.Build.props` turns on the trim, AOT and single-file analysers for
**every** project. That means:

- **A Windows provider that uses COM will produce analyser warnings, and
  `TreatWarningsAsErrors` turns those into build failures.** This is a feature —
  it catches the problem at build time rather than as a `PlatformNotSupported`
  at runtime — but it will feel like an obstacle on day one.
- Prefer **`[LibraryImport]` source-generated P/Invoke** over `[DllImport]`, and
  **`ComWrappers`-based source-generated COM** over `Marshal.GetActiveObject`
  style interop. Both are AOT-clean; the older styles are not.
- **Test the published binary, not just the debug build.** The Linux side learned
  this the hard way: a clean `dotnet build` says nothing about whether the AOT
  binary starts. See `BUILDING.md` §4.

---

## 7. Suggested order

1. ~~**Prove the scaffolding.**~~ **DONE, 3 August 2026.** `Heimdall.Windows`
   exists, both configurations and the neither-selected case were built on
   Windows, and the property-function `Condition` syntax §9 doubted is proven —
   see §2. `WindowsPlatform` returns `null` for all seven nullable members and
   throws `NotImplementedException` naming the interface for the other eleven,
   so the first thing built on top of it fails loudly and identifies itself.
   **Nothing runs yet**: the app throws on `platform.Properties`, which is the
   first member the constructor touches.
   **Two things turned up on the way — see §5a and §5b.** `PathRules.Normalise`
   does not unify `\` and `/`, which breaks `Same` on Windows, and the
   `PathRules` tests fail 7 of 56 here. Do 5a before step 3.
2. ~~**`PathRules` in Core**, and route the 15 sites through it.~~ **DONE,
   31 July 2026.** `Heimdall.Core/FileSystem/PathRules.cs` answers the four
   questions this application asks about a path's shape — `IsRoot`, `Normalise`,
   `Parent`, `LeafName`, plus `Same` and `Ancestors` — without assuming the
   separator. All fifteen sites route through it; Linux behaviour is unchanged,
   verified case by case against the inline code it replaced.
   **`Same` uses `OrdinalIgnoreCase` on Windows and `Ordinal` on Linux**, because
   two paths differing only in case are one folder on NTFS and two on ext4.
   **The `file://` conversion in `FileClipboard` deliberately still splits on
   `/`** — a URI is not a path, and Windows exchanges files as CF_HDROP anyway.
3. **`IFileSystemProvider` + `IPlacesProvider`.** First light: a window that
   lists `C:\` and has drives in the sidebar. Icons will be the drawn fallbacks
   and that is fine.
4. **`IApplicationLauncher`, `IFileOperations` (no trash), `ISearchProvider`.**
   Now it is usable.
5. **`IThemeProvider`.** Cheap, and makes it stop looking alien.
6. **Then the hard three** — trash, tags, icons — each as its own decision.

---

## 8. What to verify, and how

- **`dotnet build` on Linux must still pass** after every step. The conditional
  references make it possible to break the Linux build from a Windows machine
  without noticing. **CI covers this** — `.github/workflows/build.yml` runs on
  every push.
- **Run the published binary on Windows**, not `dotnet run`.
- **`HEIMDALL_TILE_DEBUG=1`** still works and is still the ground truth for
  whether a listing is virtualizing.
- The `[heimdall]` diagnostic lines all go to **stderr** — on Windows, run from a
  terminal or they vanish.

---

## 9. Things this document is not sure about

Stated plainly so they are not mistaken for findings:

- ~~The conditional `ProjectReference` syntax in §2 is **untested**.~~
  **Settled 3 August 2026: it works as written.** `$([System.OperatingSystem]::IsWindows())`
  and `$([System.OperatingSystem]::IsLinux())` both evaluate correctly in a
  `Condition` under the .NET 10 SDK. The MSBuild intrinsic
  `$([MSBuild]::IsOSPlatform('Windows'))` was probed alongside and also works —
  either is fine.
- ~~Whether `net10.0-windows` is worth adopting over plain `net10.0`.~~
  **Deferred deliberately, and `Heimdall.Windows` is on plain `net10.0` for
  now.** `SupportedOSPlatform` already satisfies the platform analyser, and the
  single TFM keeps the project in the solution on Linux, so CI compile-checks
  the Windows code on every push — worth more than the WinForms/WPF interop
  surface, which nothing here wants.
  **The forcing question is the registry**, which §4 needs for `IThemeProvider`:
  `Microsoft.Win32.Registry` is not in the default reference set for plain
  `net10.0`. Decide at step 5 between the `net10.0-windows` TFM and the
  standalone package — and note the TFM costs the free Linux compile-check
  above.
- Whether Avalonia's Windows backend needs anything beyond the existing package
  references. It should not — `Avalonia.Desktop` covers all three desktop
  backends — but that is an assumption, not a verified fact.
