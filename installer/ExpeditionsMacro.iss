#ifndef AppVersion
  #define AppVersion "1.0.6"
#endif
#ifndef AppNumericVersion
  #define AppNumericVersion AppVersion
#endif
#ifndef RepositoryRoot
  #define RepositoryRoot ".."
#endif
#ifndef OutputDir
  #define OutputDir RepositoryRoot + "\artifacts\release"
#endif

[Setup]
AppId={{9B68B68C-6A2A-472E-B688-53BB10C3357E}
AppName=Expeditions Macro
AppVersion={#AppVersion}
AppPublisher=Expeditions Macro contributors
AppPublisherURL=https://github.com/Bluffy420/Expedition-Macro
AppSupportURL=https://github.com/Bluffy420/Expedition-Macro/issues
AppUpdatesURL=https://github.com/Bluffy420/Expedition-Macro/releases
DefaultDirName={localappdata}\Programs\Expeditions Macro
DefaultGroupName=Expeditions Macro
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=ExpeditionsMacro-{#AppVersion}-win-x64-setup
SetupIconFile={#RepositoryRoot}\assets\app.ico
UninstallDisplayIcon={app}\ExpeditionsMacro.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
LicenseFile={#RepositoryRoot}\LICENSE.md
VersionInfoVersion={#AppNumericVersion}.0
VersionInfoCompany=Expeditions Macro contributors
VersionInfoDescription=Expeditions Macro installer
VersionInfoProductName=Expeditions Macro
VersionInfoProductVersion={#AppNumericVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#RepositoryRoot}\artifacts\publish\ExpeditionsMacro\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Expeditions Macro"; Filename: "{app}\ExpeditionsMacro.exe"
Name: "{autodesktop}\Expeditions Macro"; Filename: "{app}\ExpeditionsMacro.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\ExpeditionsMacro.exe"; Description: "Launch Expeditions Macro"; Flags: nowait postinstall skipifsilent
