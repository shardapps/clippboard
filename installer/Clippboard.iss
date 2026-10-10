#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif

[Setup]
AppId={{52BFACD5-1656-42C4-B488-96B815B572DA}
AppName=Clippboard
AppVersion={#AppVersion}
AppPublisher=Shard Apps
AppPublisherURL=https://github.com/shardapps
AppSupportURL=https://github.com/shardapps/clippboard/issues
DefaultDirName={localappdata}\Programs\Clippboard
DefaultGroupName=Clippboard
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=Clippboard-Setup-{#AppVersion}
SetupIconFile=..\assets\brand\clippboard.ico
UninstallDisplayIcon={app}\Clippboard.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Files]
Source: "..\artifacts\portable\win-x64\Clippboard.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"

[Icons]
Name: "{group}\Clippboard"; Filename: "{app}\Clippboard.exe"
Name: "{group}\Uninstall Clippboard"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\Clippboard.exe"; Description: "Open Clippboard"; Flags: nowait postinstall skipifsilent

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Clippboard"; Flags: uninsdeletevalue
