; Local Display Host - Inno Setup script
; Build the app first: dotnet publish LocalDisplayHost\LocalDisplayHost.csproj -p:PublishProfile=win-x64
; Then compile this script in Inno Setup (or: "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" LocalDisplayHost.iss)

#define MyAppName "Local Display Host"
#define MyAppExe "LocalDisplayHost.exe"
#define MyAppPublisher "Local Display Host"
; Bump this version when you release an update (e.g. 1.0.1, 1.1.0)
#define MyAppVersion "1.0.0"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
OutputDir=.
OutputBaseFilename=LocalDisplayHostSetup-{#MyAppVersion}
SetupIconFile=
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
; Uninstall previous version when installing a newer one (same AppId, higher version)
UninstallDisplayName={#MyAppName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Publish output is in ..\publish\win-x64\ (relative to this script in installer\)
Source: "..\..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
