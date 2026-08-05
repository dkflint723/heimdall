; Inno Setup script for the Windows build.
;
; The Linux side ships a tarball plus install.sh, which suits a system where
; unpacking to /opt and dropping a .desktop file is a normal way to install
; something. Windows has no such convention: a bare zip leaves the user to pick
; a directory, make their own Start menu entry, and find out for themselves how
; to remove it. So this produces a single signed-shaped .exe that does the usual
; things and, more importantly, can be uninstalled from Settings like anything
; else.
;
; Compiled by ISCC, which is preinstalled on GitHub's windows runners:
;
;   iscc /DAppVersion=0.4.0 /DPayload=..\path\to\publish packaging\heimdall.iss
;
; AppVersion and Payload are required; the compile fails with a readable message
; below rather than producing an installer stamped 0.0.0 or holding nothing.

#ifndef AppVersion
  #error AppVersion is required: pass /DAppVersion=x.y.z
#endif

#ifndef Payload
  #error Payload is required: pass /DPayload=<the publish directory>
#endif

#define AppName "Heimdall"
#define AppPublisher "dkflint723"
#define AppUrl "https://github.com/dkflint723/heimdall"
#define AppExe "Heimdall.Ui.exe"

[Setup]
AppId={{8F3C1E42-6B7A-4D59-9E2C-A1F4B8D70C36}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

; Per-user by default, so installing needs no administrator and no UAC prompt.
; A file manager is a personal tool and there is nothing here that belongs to
; the machine -- no service, no driver, no shared component. `lowest` keeps the
; installer itself unelevated; a user who wants it for everyone can still choose
; that on the first page.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; The mark on the executable, the installer and the entry in Installed apps.
SetupIconFile=..\brand\icons\heimdall.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=heimdall-{#AppVersion}-win-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; NativeAOT publishes x64 here, so refuse the architectures it will not run on
; rather than installing something that fails when double-clicked. ARM64 is
; included because Windows emulates x64 on it.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; \
    GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The publish DIRECTORY is the deliverable, not the executable: libSkiaSharp.dll
; and libHarfBuzzSharp.dll are loaded from beside the binary, and shipping the
; .exe alone produces something that aborts before it draws anything. The Linux
; job checks for the .so for the same reason.
;
; PDBs are excluded deliberately -- they are five times the size of everything
; else here and are of no use on a user's machine.
Source: "{#Payload}\*"; DestDir: "{app}"; \
    Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

; MIT requires the notice to travel with "all copies or substantial portions",
; so it ships inside the installer rather than only in the repository.
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
    Flags: nowait postinstall skipifsilent

; No [UninstallDelete]. Tabs, pinned places, window position, folder views,
; recents and the tag index all live under %LOCALAPPDATA%\heimdall, not beside
; the binary, and an uninstall deliberately leaves them there -- reinstalling or
; upgrading should find your tabs where you left them, and silently destroying
; the tag index is not something an uninstaller should decide on its own.
