; App Center - Inno Setup script
;
; Not meant to be compiled by hand: deploy.bat publishes the app, works out the
; version from AppCenter.csproj and passes everything below in on the command
; line. To build it yourself anyway:
;
;   ISCC.exe /DAppVersion=1.0.0 installer\AppCenter.iss
;
; Installs per-user into %LOCALAPPDATA%\Programs\AppCenter, so no UAC prompt for
; the app itself. The one exception is a machine that has no .NET 10 Desktop
; Runtime - see PrepareToInstall at the bottom.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\bin\Release\net10.0-windows\win-x64\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\releases"
#endif
; Read out of the built exe's version resource rather than written here, so the
; publisher has exactly one home: <Company> in AppCenter.csproj. Requires the
; publish to have run first, which deploy.bat guarantees.
#ifndef Publisher
  #define Publisher GetStringFileInfo(SourceDir + "\AppCenter.exe", COMPANY_NAME)
#endif
#if Publisher == ""
  #error Could not read CompanyName from AppCenter.exe - has the project been published?
#endif
#ifndef AppUrl
  #define AppUrl "https://github.com/RaZiel-no/AppCenter"
#endif

#define AppName        "App Center"
#define AppExeName     "AppCenter.exe"
#define RuntimeName    ".NET 10 Desktop Runtime"
#define RuntimeMajor   "10"
#define RuntimeUrl     "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"
#define RuntimePage    "https://dotnet.microsoft.com/download/dotnet/10.0"

[Setup]
; Never change AppId - it is how Windows (and winget) recognise an existing
; install as the same product across versions.
AppId={{8F3C1B62-4A7D-4E29-9C6E-1D5B0A7F2E84}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}

DefaultDirName={autopf}\AppCenter
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest

; winget is 64-bit Windows 10 1809+ only, and so is the app it drives.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

OutputDir={#OutputDir}
OutputBaseFilename=AppCenter-{#AppVersion}-Setup
SetupIconFile=..\AppCenter.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; If App Center is running, offer to close it rather than leaving files locked.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; LICENSE arrives here through the publish output, so the GPL text is installed
; alongside the binary. Deliberately not wired up as LicenseFile= : the GPL is
; not an EULA and does not want click-through acceptance to use the program, so
; the wizard ships it rather than gating on it. About links to this copy.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; App Center updating itself from a GitHub release runs this setup silently
; and exits so its files can be replaced; /RELAUNCH=1 asks to be started again
; when that is done. Nothing else passes the switch, so a hand-run or winget
; install is unaffected.
Filename: "{app}\{#AppExeName}"; Flags: nowait; Check: RelaunchRequested

[Code]

function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ ---- .NET 10 Desktop Runtime -------------------------------------------------

  The app is published framework-dependent, so the shared runtime has to be
  there. Detection is by directory rather than registry: the runtime installer's
  registry layout has moved between releases, but a versioned folder under
  shared\Microsoft.WindowsDesktop.App is what the host actually resolves
  against. The second root covers x64-on-ARM64, where x64 lands in dotnet\x64. }

function DesktopRuntimePresent: Boolean;
var
  Roots: array[0..1] of String;
  Rec: TFindRec;
  I: Integer;
begin
  Result := False;
  Roots[0] := ExpandConstant('{commonpf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';
  Roots[1] := ExpandConstant('{commonpf64}') + '\dotnet\x64\shared\Microsoft.WindowsDesktop.App';

  for I := 0 to 1 do
  begin
    if FindFirst(Roots[I] + '\{#RuntimeMajor}.*', Rec) then
    try
      repeat
        if (Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Log('Found runtime: ' + Roots[I] + '\' + Rec.Name);
          Result := True;
          Exit;
        end;
      until not FindNext(Rec);
    finally
      FindClose(Rec);
    end;
  end;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if (ProgressMax > 0) and not WizardSilent then
    WizardForm.StatusLabel.Caption :=
      Format('Downloading the {#RuntimeName} - %d%%', [(Progress * 100) div ProgressMax]);
  Result := True;
end;

{ Runs on the "Preparing to Install" step, which - unlike the wizard pages - is
  reached in /SILENT and /VERYSILENT runs too, so a winget install gets the
  runtime as well. A non-empty result aborts setup with that text as the reason.

  The runtime installs machine-wide and elevates itself, so a machine missing it
  sees one UAC prompt even though App Center itself is per-user. }

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Installer: String;
  ExitCode: Integer;
begin
  Result := '';
  if DesktopRuntimePresent then
    Exit;

  Log('{#RuntimeName} not found - fetching it.');
  Installer := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');

  if not WizardSilent then
    WizardForm.StatusLabel.Caption := 'Downloading the {#RuntimeName}...';

  try
    DownloadTemporaryFile('{#RuntimeUrl}', 'windowsdesktop-runtime.exe', '', @OnDownloadProgress);
  except
    Result := 'App Center needs the {#RuntimeName}, and it could not be downloaded.' + #13#10#13#10 +
              GetExceptionMessage + #13#10#13#10 +
              'Install it from {#RuntimePage} and run this setup again.';
    Exit;
  end;

  if not WizardSilent then
    WizardForm.StatusLabel.Caption := 'Installing the {#RuntimeName}...';

  if not Exec(Installer, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ExitCode) then
  begin
    Result := 'Could not start the {#RuntimeName} installer (' + SysErrorMessage(ExitCode) + ').';
    Exit;
  end;

  Log(Format('Runtime installer exit code: %d', [ExitCode]));
  case ExitCode of
    0, 1638: ;                        { installed, or already present }
    3010: NeedsRestart := True;       { installed, wants a reboot }
  else
    Result := Format('The {#RuntimeName} installer failed with code %d.' + #13#10#13#10 +
                     'Install it from {#RuntimePage} and run this setup again.', [ExitCode]);
  end;
end;
