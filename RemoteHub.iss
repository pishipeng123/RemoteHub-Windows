; ============================================================
;  RemoteHub 1.4.0 - Inno Setup 7 installer script
;  Compile:  ISCC.exe RemoteHub.iss
;  Output :  dist\RemoteHub-1.4.0-Setup.exe
;  NOTE   :  this file must be UTF-8 with BOM (Inno 6.3+ / 7).
; ============================================================

#define AppName "RemoteHub"
#define AppVersion "1.4.0"
#define AppExe "RemoteHub.exe"

[Setup]
AppId={{A7B3C1E4-5D82-4F90-9C61-2E8B4D0F7A13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=RemoteHub
DefaultDirName={autopf}\RemoteHub
DefaultGroupName=RemoteHub
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=RemoteHub 1.4.0
OutputDir=dist
OutputBaseFilename=RemoteHub-1.4.0-Setup
SetupIconFile=RemoteHub.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
ShowLanguageDialog=no
UsedUserAreasWarning=no
VersionInfoVersion=1.4.0.0
VersionInfoCompany=RemoteHub
VersionInfoDescription=RemoteHub 1.4.0 安装程序
VersionInfoProductName=RemoteHub
VersionInfoProductVersion=1.4.0.0
VersionInfoCopyright=Copyright (C) 2026 RemoteHub

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: unchecked
Name: "startup"; Description: "开机时自动启动 RemoteHub"; GroupDescription: "附加任务:"; Flags: unchecked

[Files]
; 安装版不放 portable.flag —— 台账按标准位置存到 %APPDATA%\RemoteHub
Source: "{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "UserGuide.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "RemoteHub.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\RemoteHub"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; IconIndex: 0
Name: "{group}\使用说明"; Filename: "{app}\UserGuide.txt"
Name: "{group}\卸载 RemoteHub"; Filename: "{uninstallexe}"
Name: "{autodesktop}\RemoteHub"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\{#AppExe}"; IconIndex: 0; Tasks: desktopicon

[Registry]
; 可选开机自启，卸载时自动清除该值
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "RemoteHub"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "立即启动 RemoteHub"; Flags: nowait postinstall skipifsilent

; 卸载默认保留 %APPDATA%\RemoteHub 台账（安全默认）。
; 如需彻底清理，用户可手动删除该目录，说明见 UserGuide.txt。
