; DOC2MD Windows x64 installer.
; Build-Installer.ps1 supplies the staged payload, version, and output paths.

#ifndef PayloadDir
  #define PayloadDir "..\artifacts\installer-build\payload"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef VersionInfoVersion
  #define VersionInfoVersion "1.0.0.0"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "DOC2MD-1.0.0-win-x64-Setup"
#endif

#define AppName "DOC2MD"
#define AppPublisher "Taskscape Ltd"
#define GuiExeName "DOC2MD.Gui.exe"
#define CliExeName "DOC2MD.Cli.exe"
#define ApiExeName "DOC2MD.Api.exe"
#define McpExeName "DOC2MD.Mcp.exe"

[Setup]
AppId={{C1AF58DE-4E49-4F3D-9FAB-2EE6A20AF283}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\Taskscape\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
CloseApplicationsFilter={#GuiExeName},{#CliExeName},{#ApiExeName},{#McpExeName},soffice.exe,python.exe
RestartApplications=no
UninstallDisplayIcon={app}\{#GuiExeName}
VersionInfoVersion={#VersionInfoVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "polish"; MessagesFile: "compiler:Languages\Polish.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked
Name: "addtopath"; Description: "Add DOC2MD to the current user's PATH"; GroupDescription: "Command line integration:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#GuiExeName}"; WorkingDir: "{app}"
Name: "{autoprograms}\DOC2MD README"; Filename: "{app}\README.md"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#GuiExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath(ExpandConstant('{app}')); Tasks: addtopath

[Run]
Filename: "{app}\{#GuiExeName}"; Description: "Launch {#AppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function NeedsAddPath(const Directory: String): Boolean;
var
  CurrentPath: String;
begin
  if not RegQueryStringValue(HKEY_CURRENT_USER, 'Environment', 'Path', CurrentPath) then
  begin
    Result := True;
    Exit;
  end;

  Result := Pos(';' + Uppercase(Directory) + ';', ';' + Uppercase(CurrentPath) + ';') = 0;
end;

procedure RequireInstalledFile(const RelativePath, Description: String);
begin
  if not FileExists(ExpandConstant('{app}\' + RelativePath)) then
    RaiseException(Description + ' was not installed: ' + RelativePath);
end;

procedure VerifyInstalledPayload;
var
  ResultCode: Integer;
begin
  RequireInstalledFile('{#GuiExeName}', 'The DOC2MD GUI');
  RequireInstalledFile('{#CliExeName}', 'The DOC2MD CLI');
  RequireInstalledFile('{#ApiExeName}', 'The DOC2MD API');
  RequireInstalledFile('{#McpExeName}', 'The DOC2MD MCP server');
  RequireInstalledFile('.markitdown-venv\Scripts\python.exe', 'The bundled Python runtime');
  RequireInstalledFile('.markitdown-venv\Scripts\Lib\site-packages\markitdown\__init__.py', 'The MarkItDown Python package');
  RequireInstalledFile('tessdata\eng.traineddata', 'The English OCR model');
  RequireInstalledFile('tessdata\pol.traineddata', 'The Polish OCR model');
  RequireInstalledFile('runtime\libreoffice\program\soffice.exe', 'The bundled LibreOffice runtime');

  if not Exec(
    ExpandConstant('{app}\.markitdown-venv\Scripts\python.exe'),
    '-c "from markitdown import MarkItDown; import bs4, mammoth, openpyxl, pandas, pdfplumber, pptx, xlrd"',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
    RaiseException('The bundled Python runtime could not be started.')
  else if ResultCode <> 0 then
    RaiseException(Format('The bundled Python libraries failed their installation check (exit code %d).', [ResultCode]));

  if not Exec(
    ExpandConstant('{app}\runtime\libreoffice\program\soffice.exe'),
    '--headless --version',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
    RaiseException('The bundled LibreOffice runtime could not be started.')
  else if ResultCode <> 0 then
    RaiseException(Format('The bundled LibreOffice runtime failed its installation check (exit code %d).', [ResultCode]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    VerifyInstalledPayload;
end;
