# Vaktari — three defects found from outside, and one deferred move

Written 15 August 2026 from the IconThemeEngine session, which references
`Vaktari.Core` rather than forking it and therefore exercises it against themes
Vaktari's own tests have not seen.

Vaktari is at `d17e009` ("Changelog for 0.9.3"), clean tree. Everything below was
measured on Windows 11 25H2 build 26200, not recalled. Line numbers are from that
commit.

Two of these are bugs in shipped behaviour, one is a comment that is not true,
and the fourth is the `IconThemeInstaller` move that HANDOFF.md section 2 asked
for and this session deliberately did not do.

---

## 1. Alias chains resolve one hop, and real themes need several

**`src/Vaktari.Core/FileSystem/FreedesktopIconTheme.cs`, `AddAliases`, line 338.**

```csharp
if (File.Exists(to))
{
    Add(map, Path.GetFileNameWithoutExtension(from), to);
}
else if (Directory.Exists(to) && folders.Add(to))
```

A target that is itself an alias is not a file on disk — it is only another line
in `.vaktari-aliases` — so `File.Exists` is false, `Directory.Exists` is false,
and the entry is silently dropped.

### What it costs

**Kora** (KDE Store id 1256209, by tarma, 940,547 downloads) is rejected by
Vaktari as not an icon theme at all. It has **830 link-to-link chains**, and its
chain for the generic text icon is three deep:

```
mimetypes/scalable/text-x-generic.svg
  -> mimetypes/scalable/application-text.svg
  -> mimetypes/scalable/application-document.svg   <-- a real file
```

All three names `FromFolder` probes with — `text-x-generic`, `text-plain`,
`application-x-generic` — are chained like that, so the probe resolves nothing,
`FromFolder` returns null, and the theme never appears in Settings. `folder.svg`
in the same theme *is* a real file and would have resolved perfectly; the theme
is fine and the reader cannot see it.

This is the trap HANDOFF.md section 6 already names — *"its 48-pixel folder is a
link to a link, and following one hop finds a file that is itself only another
name"* — but the fix that was made for it, the `VariantBase` / `alsoInherits`
fallback, cannot help here. That works because Papirus-Dark has Papirus sitting
beside it. Kora's chains are internal and it has no sibling base, so there is
nothing behind it to fall through to.

### Shape of the fix

Follow the chain before deciding whether the target is a file or a directory,
with a depth limit for the same reason `BuildSearchOrder` has one: a malformed
index can describe a cycle. Chains terminate at real files — sampled depths on
Kora were 1 to 3 — so a small limit is enough and a large one is a liability.

The index is already fully in hand when this runs, so resolution needs no extra
I/O: read the whole file into a `from -> to` dictionary first, then walk it.

### Testing it

A hand-built two-hop chain does catch this one, which is unusual for this
codebase — but section 6's rule still applies, so confirm against Kora as well.
It installs in about fifteen seconds:

```
https://api.kde-look.org/ocs/v1/content/data/1256209?format=json
```

`downloadlink1` is `kora-2-0-4.tar.xz`, 2.5 MB. Unpacks to 9,791 icons and 13,690
aliases. After the fix, `FreedesktopIconTheme.FromFolder` on the `kora` folder
should return non-null and `Resolve(["text-x-generic"], 48)` should reach
`application-document.svg`.

**Do not "fix" this by loosening the probe in `FromFolder`.** The probe is
correct and is what stops a folder of unrelated pictures being offered as a
theme. What is wrong is the alias resolution beneath it.

---

## 2. The download progress bar never moves for the theme in the catalogue

**`src/Vaktari.Ui/Settings/IconThemeInstaller.cs`, `CountingStream.Report`,
line 130.**

```csharp
if (read <= 0 || expected is not { } total || total <= 0) return read;
```

No `Content-Length` means no report at all, ever.

**GitHub does not send one for these archives.** Measured against the exact URL
in `IconThemeCatalogue.All`:

```
GET https://github.com/PapirusDevelopmentTeam/papirus-icon-theme/archive/refs/heads/master.tar.gz
  -> 302 to codeload.github.com
  -> 200, Content-Type: application/x-gzip
     Transfer-Encoding: chunked
     Content-Length:   <absent>
```

The tarball is generated on the fly. So the one theme Vaktari ships in its
catalogue is precisely the case where the Settings progress bar sits at zero for
the whole 110 MB, which reads as a hung download rather than a working one.

Worth fixing rather than removing: **pling.com, which hosts the KDE Store's
files, does send `Content-Length`**, so both branches are real once the store is
reachable at all.

### Shape of the fix

Bytes are always known; the fraction is the extra. `IconThemeEngine` uses:

```csharp
public readonly record struct FetchProgress(long Bytes, long? Total)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Bytes / Total.Value, 0, 1) : null;
}
```

and throttles on whole percents where a total is known, whole megabytes where it
is not — the same reasoning as the existing comment about not posting to the
dispatcher a hundred thousand times. `IProgress<double>` becomes
`IProgress<FetchProgress>`, and `SettingsViewModel` shows an indeterminate bar
plus a megabyte count when `Fraction` is null.

---

## 3. A comment that is not true

**`src/Vaktari.Ui/Settings/IconThemeInstaller.cs`, lines 44–45.**

```csharp
// GitHub redirects archive downloads to a storage host and refuses
// requests without one.
```

The redirect is real — `codeload.github.com`, confirmed above. The refusal is
not: both `HEAD` and `GET` to that URL return 200 with no `User-Agent` header at
all. Keep sending one, but the comment currently states as a requirement
something that is politeness, and the next person to read it will believe it.

---

## 4. Deferred: move `IconThemeInstaller` into `Vaktari.Core`

HANDOFF.md section 2 asked for this — *"it lives in Vaktari.Ui today but has no
UI dependency ... so move it into the shared library rather than copying it"* —
and it is still the right end state. It was **not** done from the
IconThemeEngine side, on purpose: vaktari was mid-0.9.3 with a clean tree, and
the move is not quite mechanical.

What it involves:

- The file is `src/Vaktari.Ui/Settings/IconThemeInstaller.cs`.
- Five call sites, all fully qualified as `Vaktari.Ui.Settings.IconThemeInstaller`
  — two in `src/Vaktari.Ui/ViewModels/SettingsViewModel.cs` (lines 445, 499),
  three in `tests/Vaktari.Ui.Tests/IconThemeFetchTests.cs` (lines 25, 26, 201).
- **`PackName` is `internal` and asserted at `IconThemeFetchTests.cs:201`.**
  Moving the class to Vaktari.Core breaks that test unless Vaktari.Core gains an
  `InternalsVisibleTo` for `Vaktari.Ui.Tests`, or the assertion moves to
  `Vaktari.Core.Tests`. The second is tidier.

Meanwhile IconThemeEngine has its own `ThemeInstaller` — about sixty lines of
`HttpClient` and a counting stream. **`IconThemeArchive` is referenced, not
copied**, so nothing that section 6 warns about exists twice; what is duplicated
is download plumbing and a `PackName`. If the move happens, IconThemeEngine can
delete its copy and call the shared one, and item 2 above gets fixed once instead
of twice.

---

## 5. What else was learned that Vaktari may care about

None of these are Vaktari bugs, but they were paid for and are cheaper to read
than to rediscover.

- **`SystemFileAssociations` does not drive file icons on Windows 11 25H2.**
  HANDOFF.md section 3(c) called it "the correct lever". Writing the per-extension
  and per-perceived-type keys under HKCU has no effect: verified with `.cs`, which
  has no class, no `UserChoice` and `PerceivedType=text`, and still drew
  `shell32.dll,0` with both keys present in the merged `HKCR` view. Only
  `<class>\DefaultIcon` works.
- **`FileExts\<ext>\UserChoice` names a different class from the extension key,
  and which one supplies the icon varies.** `.pdf` takes its icon from the
  extension's class while `UserChoice` says otherwise; `.zip` has no class in
  `HKCR` at all and takes its icon from `UserChoice`. Writing both is cheaper
  than inferring a rule from two data points.
- **Windows records a MIME type for 422 of 1104 extensions**, and a freedesktop
  icon name is a MIME type with the slash swapped for a hyphen. `HKCR\.ext\Content Type`
  is therefore a mapping table nobody had to write, and it covers types no
  hand-written table would list.
- **`probe-windows-icons.ps1` must be run under `pwsh`.** It is UTF-8 without a
  BOM, and Windows PowerShell 5.1 reads its em dashes as CP-1252, which unbalances
  a quote and fails the parse. A BOM was added to IconThemeEngine's copy.

---

## 6. Suggested order

1. **Alias chains.** It is the only one that makes a working theme invisible, and
   it affects Vaktari's own Settings window, not just the other repo.
2. **Progress reporting**, which is a visible defect on the single theme the
   catalogue ships.
3. **The comment**, which is one line.
4. **The move**, when 0.9.3 is at a natural stopping point — it touches a test
   and is better as its own reviewed commit than as a rider on a bug fix.
