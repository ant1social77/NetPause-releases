; Установщик NetPause (Inno Setup 6). Сборка: build-installer.ps1
; Программа ставится в Program Files, данные пользователей живут отдельно
; (C:\ProgramData\NetPause и %LOCALAPPDATA%\NetPause) — обновление их не трогает.

#ifndef AppVer
  #define AppVer "1.0.0"
#endif

[Setup]
AppId={{B6F3D0B2-5C41-4E8A-9A3E-6E2B1D4C9F10}
AppName=NetPause
AppVersion={#AppVer}
AppVerName=NetPause {#AppVer}
AppPublisher=NetPause
VersionInfoVersion={#AppVer}
DefaultDirName={autopf}\NetPause
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=installer
OutputBaseFilename=NetPause-Setup-{#AppVer}
SetupIconFile=NetPause.ico
UninstallDisplayIcon={app}\NetPause.exe
UninstallDisplayName=NetPause
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Messages]
ru.WelcomeLabel2=NetPause бережёт трафик: показывает, какие программы тратят интернет, и выключает лишние одним переключателем.%n%nВо время установки Windows один раз спросит разрешение — нажмите «Да». Больше программа его спрашивать не будет.

[Tasks]
Name: "autostart"; Description: "Запускать NetPause вместе с Windows"
Name: "desktopicon"; Description: "Значок на рабочем столе"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\NetPause"; Filename: "{app}\NetPause.exe"
Name: "{autodesktop}\NetPause"; Filename: "{app}\NetPause.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\NetPause.exe"; Parameters: "--autostart-on"; Description: "Открыть NetPause"; Tasks: autostart; Flags: nowait postinstall
Filename: "{app}\NetPause.exe"; Description: "Открыть NetPause"; Tasks: not autostart; Flags: nowait postinstall

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM NetPause.exe"; Flags: runhidden; RunOnceId: "StopApp"
Filename: "{app}\NetPause.exe"; Parameters: "--cleanup"; Flags: waituntilterminated; RunOnceId: "Cleanup"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN NetPause /F"; Flags: runhidden; RunOnceId: "Autostart"

[Code]
procedure StopRunning();
var
  Code: Integer;
begin
  { работающую копию останавливаем; её блокировки и пометки снимаем сами,
    новая версия после запуска вернёт всё по сохранённым настройкам }
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM NetPause.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
  if FileExists(ExpandConstant('{app}\NetPause.exe')) then
    Exec(ExpandConstant('{app}\NetPause.exe'), '--cleanup', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec('powershell.exe', '-NoProfile -Command "Remove-NetFirewallRule -Group NetPause -ErrorAction SilentlyContinue"',
       '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunning();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    if MsgBox('Удалить и ваши данные NetPause: статистику, сети, настройки и сохранённые входы на трекеры?' + #13#10 +
              'Если собираетесь поставить NetPause снова, нажмите «Нет» — всё сохранится.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{commonappdata}\NetPause'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\NetPause'), True, True, True);
    end;
end;
