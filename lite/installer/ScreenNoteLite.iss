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
OutputBaseFilename={#OutputName}
Compression=lzma2/max
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
#include RuntimeCleanup

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    RemoveObsoleteRuntime();
end;

#ifdef LightBuild
function HasDesktopRuntimeAt(Root: String): Boolean;
var
  Item: TFindRec;
  Shared: String;
begin
  Result := False;
  if Root = '' then Exit;
  Shared := AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\';
  if FindFirst(Shared + '10.*', Item) then
  begin
    try
      repeat
        if FileExists(Shared + Item.Name + '\PresentationFramework.dll') then
          Result := True;
      until not FindNext(Item);
    finally
      FindClose(Item);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  Versions: TArrayOfString;
  Index: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions) then
    for Index := 0 to GetArrayLength(Versions) - 1 do
      if Pos('10.', Versions[Index]) = 1 then
        Result := True;
  { SDK/ZIP installations can supply a valid runtime without registry entries. }
  Result := Result or HasDesktopRuntimeAt(ExpandConstant('{commonpf64}\dotnet'))
    or HasDesktopRuntimeAt(GetEnv('DOTNET_ROOT_X64'))
    or HasDesktopRuntimeAt(GetEnv('DOTNET_ROOT'));
  if not Result then
    SuppressibleMsgBox('轻量版需要 .NET 10 桌面运行环境。请先安装该环境，或改用自带运行环境的拾屏安装包。', mbError, MB_OK, IDOK);
end;
#endif

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
