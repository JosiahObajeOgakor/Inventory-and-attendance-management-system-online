; ChewyStock (ChewyPetsFeed) Windows installer — Inno Setup 6.
; One self-contained setup .exe: installs the app AND its database engine
; (Microsoft SQL Server Express LocalDB, bundled — no internet needed), then
; creates the database + schema. Every PC gets its own local database.
;
; Build:  1) dotnet build -c Release            (from the repo root)
;         2) ISCC.exe installer\StockDesk.iss
; Output: installer\output\ChewyStock-Setup.exe
;
; Requires installer\redist\SqlLocalDB.msi (SQL Server 2022 Express LocalDB, x64):
; https://download.microsoft.com/download/3/8/d/38de7036-2433-4207-8eae-06e247e17b25/SqlLocalDB.msi

#define MyAppName "ChewyStock"
#define MyAppPublisher "ChewyPetsFeed"
#define MyAppExeName "ChewyStock.exe"
#define MyAppVersion "1.11.0"
#define ReleaseDir "..\bin\Release\net48"

[Setup]
; Same AppId as the earlier "StockDesk" builds, so this upgrades them in place.
AppId={{6F2C9C2E-6B1E-4E7C-9C1B-STOCKDESK0001}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
; Don't reuse the old "StockDesk" folder / Start-menu group from earlier builds.
UsePreviousAppDir=no
UsePreviousGroup=no
DisableProgramGroupPage=yes
; Windows 10 version 1903 or later (ships .NET Framework 4.8), 64-bit only —
; the bundled LocalDB engine is x64-only.
MinVersion=10.0.18362
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=ChewyStock-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Branding: Chewy Pet icon on the setup file, logo artwork inside the wizard.
SetupIconFile=..\Assets\app.ico
WizardImageFile=art\wizard.bmp,art\wizard@2x.bmp
WizardSmallImageFile=art\wizard-small.bmp,art\wizard-small@2x.bmp
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer, together with Microsoft SQL Server Express LocalDB — the free database engine ChewyStock keeps its data in, installed under Microsoft's license terms.%n%nYour data stays on this computer.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"

[InstallDelete]
; Leftovers from the builds named "StockDesk" (program files only — the
; customer's data lives in %LOCALAPPDATA%\StockDesk\Data and is never touched).
Type: filesandordirs; Name: "{autopf}\StockDesk"
Type: filesandordirs; Name: "{autoprograms}\StockDesk"
Type: files; Name: "{autodesktop}\StockDesk.lnk"
Type: files; Name: "{app}\StockDesk.exe"
Type: files; Name: "{app}\StockDesk.exe.config"

[Files]
; Everything from the Release build except debug symbols.
Source: "{#ReleaseDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs createallsubdirs ignoreversion
; Database engine — extracted only when this PC doesn't already have LocalDB.
Source: "redist\SqlLocalDB.msi"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: not IsLocalDBInstalled

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName} now"; Flags: nowait postinstall skipifsilent

; Uninstall removes the program only. The customer's database
; (%LOCALAPPDATA%\StockDesk\Data) and LocalDB are left in place, so a
; reinstall picks the data straight back up.

[Code]
function IsLocalDBInstalled(): Boolean;
var
  Versions: TArrayOfString;
begin
  // 64-bit install mode, so HKLM here is the 64-bit view LocalDB registers in.
  Result := RegGetSubkeyNames(HKEY_LOCAL_MACHINE,
    'SOFTWARE\Microsoft\Microsoft SQL Server Local DB\Installed Versions', Versions)
    and (GetArrayLength(Versions) > 0);
end;

function InitializeSetup(): Boolean;
var
  Release: Cardinal;
begin
  // .NET Framework 4.8 = release 528040 or later.
  Result := RegQueryDWordValue(HKEY_LOCAL_MACHINE,
    'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) and (Release >= 528040);
  if not Result then
    MsgBox('{#MyAppName} needs Microsoft .NET Framework 4.8, which is missing on this PC.' + #13#10 + #13#10 +
      'Run Windows Update (or install .NET Framework 4.8 from microsoft.com), then run this setup again.',
      mbCriticalError, MB_OK);
end;

procedure InstallLocalDB();
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing the database engine (SQL Server Express LocalDB)… this can take a few minutes.';
  if not Exec('msiexec.exe',
      '/i "' + ExpandConstant('{tmp}\SqlLocalDB.msi') + '" /qn /norestart IACCEPTSQLLOCALDBLICENSETERMS=YES',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
     or ((ResultCode <> 0) and (ResultCode <> 3010)) then
    MsgBox('The database engine could not be installed (error ' + IntToStr(ResultCode) + ').' + #13#10 + #13#10 +
      '{#MyAppName} was installed, but it needs this engine to run. Restart the PC and run this setup again. ' +
      'If it keeps failing, install "SQL Server Express LocalDB" from microsoft.com.',
      mbError, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not IsLocalDBInstalled() then
      InstallLocalDB();
    // Create the database as the signed-in user — LocalDB instances and the data
    // folder are per-user, and the elevated setup process may be a different
    // admin account. (If this can't run now, the app does it on first launch.)
    if IsLocalDBInstalled() then
    begin
      WizardForm.StatusLabel.Caption := 'Creating the {#MyAppName} database…';
      ExecAsOriginalUser(ExpandConstant('{app}\{#MyAppExeName}'), '--setup-db', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    end;
  end;
end;
