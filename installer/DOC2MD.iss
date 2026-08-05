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
#define CliExeName "DOC2MD.Cli.exe"
#define GuiExeName "DOC2MD.Gui.exe"
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
CloseApplicationsFilter={#CliExeName},{#GuiExeName},{#ApiExeName},{#McpExeName},soffice.exe,python.exe,tesseract.exe
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
Name: "addtopath"; Description: "Add DOC2MD to the current user's PATH"; GroupDescription: "Command line integration:"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs restartreplace

[Icons]
Name: "{autoprograms}\DOC2MD"; Filename: "{app}\{#GuiExeName}"

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Check: NeedsAddPath(ExpandConstant('{app}')); Tasks: addtopath

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
  RequireInstalledFile('{#CliExeName}', 'The DOC2MD CLI');
  RequireInstalledFile('{#GuiExeName}', 'The DOC2MD Avalonia GUI');
  RequireInstalledFile('{#ApiExeName}', 'The DOC2MD API');
  RequireInstalledFile('{#McpExeName}', 'The DOC2MD MCP server');
  RequireInstalledFile('Resources\python\python.exe', 'The bundled Python runtime');
  RequireInstalledFile('Resources\python\Lib\site-packages\markitdown\__init__.py', 'The MarkItDown Python package');
  RequireInstalledFile('Resources\tessdata\eng.traineddata', 'The English OCR model');
  RequireInstalledFile('Resources\tessdata\pol.traineddata', 'The Polish OCR model');
  RequireInstalledFile('Resources\tesseract\tesseract.exe', 'The bundled Tesseract runtime');
  RequireInstalledFile('Resources\libreoffice\program\soffice.exe', 'The bundled LibreOffice runtime');

  if not Exec(
    ExpandConstant('{app}\Resources\python\python.exe'),
    '-c "from markitdown import MarkItDown; import bs4, mammoth, openpyxl, pandas, pdfplumber, pptx, xlrd"',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
    RaiseException('The bundled Python runtime could not be started.')
  else if ResultCode <> 0 then
    RaiseException(Format('The bundled Python libraries failed their installation check (exit code %d).', [ResultCode]));

  if not Exec(
    ExpandConstant('{app}\Resources\libreoffice\program\soffice.exe'),
    '--headless --version',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
    RaiseException('The bundled LibreOffice runtime could not be started.')
  else if ResultCode <> 0 then
    RaiseException(Format('The bundled LibreOffice runtime failed its installation check (exit code %d).', [ResultCode]));

  if not Exec(
    ExpandConstant('{app}\Resources\tesseract\tesseract.exe'),
    '--version',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
    RaiseException('The bundled Tesseract runtime could not be started.')
  else if ResultCode <> 0 then
    RaiseException(Format('The bundled Tesseract runtime failed its installation check (exit code %d).', [ResultCode]));
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    VerifyInstalledPayload;
end;
