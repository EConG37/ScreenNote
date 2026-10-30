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
AppId={{3F6B9E4A-7C2D-4A85-9B1E-5D8C2F0A6B73}
AppName=拾屏 精简版
AppVersion={#AppVersion}
AppPublisher=ScreenNote
AppVerName=拾屏 精简版 {#AppVersion}
DefaultDirName={localappdata}\Programs\ScreenNoteLite
DefaultGroupName=拾屏 精简版
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
AppMutex=Local\ScreenNoteLite.Desktop
SetupMutex=Local\ScreenNoteLite.Installer
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
WizardStyle=modern
SetupIconFile={#PayloadDir}\assets\app.ico
UninstallDisplayIcon={app}\ScreenNoteLite.exe
OutputBaseFilename=ScreenNoteLite-{#AppVersion}-Setup-x64
Compression=lzma2/normal
SolidCompression=yes
ShowLanguageDialog=no
VersionInfoProductName=拾屏 精简版
VersionInfoDescription=拾屏 精简版 Windows 安装程序

[Languages]
Name: "zh"; MessagesFile: "{#ChineseMessages}"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Dirs]
Name: "{app}\data"; Flags: uninsneveruninstall

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{group}\拾屏 精简版"; Filename: "{app}\ScreenNoteLite.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\拾屏 精简版"; Filename: "{app}\ScreenNoteLite.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\ScreenNoteLite.exe"; Description: "启动拾屏 精简版"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Remove only an auto-start entry belonging to this installation. }
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'ScreenNoteLite', StartupCommand) then
      if CompareText(StartupCommand, '"' + ExpandConstant('{app}\ScreenNoteLite.exe') + '" --tray') = 0 then
        RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'ScreenNoteLite');
  end;
end;