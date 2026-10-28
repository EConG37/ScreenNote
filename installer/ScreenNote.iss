#ifndef AppVersion
  #error AppVersion is required
#endif
#ifndef PayloadDir
  #error PayloadDir is required
#endif
#ifndef ChineseMessages
  #error ChineseMessages is required
#endif

[Setup]
AppId={{791FC0D7-B176-4AD0-AB6E-1D2C67AB598F}
AppName=拾屏 ScreenNote
AppVersion={#AppVersion}
AppPublisher=ScreenNote
AppVerName=拾屏 ScreenNote {#AppVersion}
DefaultDirName={localappdata}\Programs\ScreenNote
DefaultGroupName=拾屏
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
AppMutex=Local\ScreenNote.Desktop
SetupMutex=Local\ScreenNote.Installer
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
WizardStyle=modern
SetupIconFile={#PayloadDir}\assets\app.ico
UninstallDisplayIcon={app}\ScreenNote.exe
OutputBaseFilename=ScreenNote-{#AppVersion}-Setup-x64
Compression=lzma2/normal
SolidCompression=yes
ShowLanguageDialog=no
VersionInfoProductName=拾屏 ScreenNote
VersionInfoDescription=拾屏 Windows 安装程序

[Languages]
Name: "zh"; MessagesFile: "{#ChineseMessages}"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Dirs]
Name: "{app}\data"; Flags: uninsneveruninstall

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,*.pyc,__pycache__\*"

[Icons]
Name: "{group}\拾屏"; Filename: "{app}\ScreenNote.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\拾屏"; Filename: "{app}\ScreenNote.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ScreenNote.exe"; Description: "启动拾屏"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Remove only an auto-start entry belonging to this installation. }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'ScreenNote', StartupCommand) then
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\ScreenNote.exe') + '" --tray') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ScreenNote');
  end;
end;
