@echo off
setlocal enabledelayedexpansion

rem  App Center - release build.
rem
rem    deploy.bat            ship the next patch: 1.0.3 in the csproj -^> 1.0.4
rem    deploy.bat 1.1.0      ship that version instead
rem
rem  Publishes framework-dependent win-x64, compiles the Inno Setup installer,
rem  zips a portable copy, and writes ready-to-submit winget manifests. It all
rem  lands in releases\.
rem
rem  <Version> in AppCenter.csproj records the last version that was released,
rem  and every release moves it on - two builds can never go out wearing the
rem  same number. It is rewritten at the very end, so a run that fails halfway
rem  leaves the number alone rather than burning it.

rem ---------------------------------------------------------------- config ---
rem  These end up in the installer's Add/Remove entry and in the winget
rem  manifests, so they are worth getting right before the first submission.
rem
rem  Publisher and copyright are deliberately NOT here: they come from <Company>
rem  and <Copyright> in AppCenter.csproj, so the name is written once and flows
rem  to the exe metadata, the installer, Add/Remove and the manifests together.

set "APP_URL=https://github.com/RaZiel-no/AppCenter"
set "PACKAGE_ID=ArnsteinSkara.AppCenter"
rem  GPL-3.0 to match Ubuntu's App Center, the project this one is modelled on.
set "LICENSE=GPL-3.0"

rem ------------------------------------------------------------------------ --

rem  Run from a PowerShell 7 terminal and PSModulePath points at pwsh's modules,
rem  which the Windows PowerShell 5.1 we shell out to below will happily load in
rem  place of its own - and then half of Microsoft.PowerShell.Utility is missing
rem  (Get-FileHash, for one). Blank it and 5.1 rebuilds its own default.
set "PSModulePath="

set "ROOT=%~dp0"
set "ROOT=%ROOT:~0,-1%"
set "PROJ=%ROOT%\AppCenter.csproj"
set "OUT=%ROOT%\releases"
set "PUBDIR=%ROOT%\bin\Release\net10.0-windows\win-x64\publish"

echo.
echo === App Center release ===
echo.

rem ---- version ---------------------------------------------------------------

set "VERSION=%~1"
if defined VERSION (
  echo Version:   %VERSION%  ^(given on the command line^)
  goto :have_version
)

rem  -Encoding UTF8 is not optional: Windows PowerShell reads a BOM-less file as
rem  ANSI, which turns the csproj's (c) into two characters of mojibake.
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content -Raw -Encoding UTF8 -LiteralPath '%PROJ%')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set "RELEASED=%%v"
if not defined RELEASED (
  echo ERROR: no ^<Version^> in AppCenter.csproj, and none given on the command line.
  goto :fail
)

rem  Bump the last component, whatever the depth: 1.0.9 -^> 1.0.10. Written with
rem  LastIndexOf rather than a regex because ^ and $ are cmd escape characters
rem  inside a for /f, and an anchored pattern would need doubling up to survive.
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "$v = '%RELEASED%'; $i = $v.LastIndexOf('.'); $n = 0; if ($i -gt 0 -and [int]::TryParse($v.Substring($i + 1), [ref]$n)) { $v.Substring(0, $i + 1) + ($n + 1) }"`) do set "VERSION=%%v"
if not defined VERSION (
  echo ERROR: cannot bump "%RELEASED%" - it does not end in a number.
  echo        Pass the version you want instead: deploy.bat 1.1.0
  goto :fail
)
echo Version:   %VERSION%  ^(last released %RELEASED%^)

:have_version

rem ---- toolchain -------------------------------------------------------------

where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: dotnet is not on PATH. Install the .NET 10 SDK.
  goto :fail
)

call :find_iscc
if defined ISCC goto :have_iscc

echo.
echo Inno Setup 6 was not found - installing it with winget...
where winget >nul 2>&1
if errorlevel 1 (
  echo ERROR: winget is not available either. Install Inno Setup 6 by hand:
  echo        https://jrsoftware.org/isdl.php
  goto :fail
)
winget install --id JRSoftware.InnoSetup --exact --source winget --accept-package-agreements --accept-source-agreements
call :find_iscc
if not defined ISCC (
  echo ERROR: still cannot find ISCC.exe after installing Inno Setup.
  goto :fail
)

:have_iscc
echo Compiler:  !ISCC!

rem ---- publish ---------------------------------------------------------------

echo.
echo --- publish ---
if exist "%PUBDIR%" rmdir /s /q "%PUBDIR%"
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained false -p:Version=%VERSION% --nologo -v minimal
if errorlevel 1 goto :fail
if not exist "%PUBDIR%\AppCenter.exe" (
  echo ERROR: publish produced no AppCenter.exe in "%PUBDIR%".
  goto :fail
)
rem catalog.json drives every page - a build without it is not shippable.
if not exist "%PUBDIR%\catalog.json" (
  echo ERROR: catalog.json is missing from the publish output.
  goto :fail
)

rem ---- installer -------------------------------------------------------------

echo.
echo --- installer ---
if not exist "%OUT%" mkdir "%OUT%"
rem  No /DPublisher - the script reads CompanyName out of the published exe.
"!ISCC!" /Q "/DAppVersion=%VERSION%" "/DSourceDir=%PUBDIR%" "/DOutputDir=%OUT%" "/DAppUrl=%APP_URL%" "%ROOT%\installer\AppCenter.iss"
if errorlevel 1 goto :fail

set "SETUP=%OUT%\AppCenter-%VERSION%-Setup.exe"
if not exist "%SETUP%" (
  echo ERROR: Inno Setup reported success but "%SETUP%" is not there.
  goto :fail
)

rem ---- portable zip ----------------------------------------------------------

echo --- portable zip ---
set "ZIP=%OUT%\AppCenter-%VERSION%-portable.zip"
rem  Filtering with Where-Object, not -Exclude: -Exclude is quietly ignored when
rem  -LiteralPath names a directory, and the pdb sails straight into the zip.
powershell -NoProfile -Command "$f = Get-ChildItem -LiteralPath '%PUBDIR%' | Where-Object { $_.Extension -ne '.pdb' }; Compress-Archive -Path $f.FullName -DestinationPath '%ZIP%' -Force"
if errorlevel 1 goto :fail

rem ---- winget manifests ------------------------------------------------------

echo --- winget manifests ---
for /f "usebackq delims=" %%h in (`powershell -NoProfile -Command "(Get-FileHash -LiteralPath '%SETUP%' -Algorithm SHA256).Hash"`) do set "SHA256=%%h"
if not defined SHA256 (
  echo ERROR: could not hash "%SETUP%".
  goto :fail
)

set "MANIFESTS=%OUT%\winget\%VERSION%"
if exist "%MANIFESTS%" rmdir /s /q "%MANIFESTS%"
mkdir "%MANIFESTS%"
call :render yaml            || goto :fail
call :render installer.yaml  || goto :fail
call :render locale.en-US.yaml || goto :fail

rem ---- published hash --------------------------------------------------------

rem  The same digest the manifest carries, as a file to attach to the GitHub
rem  release beside the installer. App Center updating itself from GitHub - ahead
rem  of winget - downloads the installer and checks it against this before
rem  running it. Standard sha256sum layout, lower-case, two spaces, file name.
set "SHASUM=%SETUP%.sha256"
powershell -NoProfile -Command "[System.IO.File]::WriteAllText('%SHASUM%', '%SHA256%'.ToLowerInvariant() + '  ' + [System.IO.Path]::GetFileName('%SETUP%') + [char]10, (New-Object System.Text.UTF8Encoding($false)))"
if errorlevel 1 (
  echo ERROR: could not write "%SHASUM%".
  goto :fail
)

rem ---- record the version ----------------------------------------------------

rem  Last, and only on the way to success: from here on this number is spent,
rem  and the next deploy starts from it. Read and written through .NET rather
rem  than Get-Content/Set-Content so the file keeps the encoding it has - the
rem  csproj is BOM-less UTF-8 and holds a (c) and an em dash, both of which a
rem  round trip through Windows PowerShell's default encodings would mangle.
powershell -NoProfile -Command "$p = '%PROJ%'; $t = [System.IO.File]::ReadAllText($p); $cur = ([xml]$t).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1; if ($cur -ne '%VERSION%') { $t = $t.Replace('<Version>' + $cur + '</Version>', '<Version>%VERSION%</Version>'); [System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($false))) }"
if errorlevel 1 (
  echo.
  echo WARNING: the release is built, but ^<Version^> in AppCenter.csproj could not
  echo          be updated to %VERSION%. Set it by hand, or the next deploy will
  echo          build this same version again.
)

rem ---- summary ---------------------------------------------------------------

echo.
echo === done ===
echo.
for %%f in ("%SETUP%" "%SHASUM%" "%ZIP%") do echo    %%~nxf   %%~zf bytes
echo.
echo    SHA256      !SHA256!
echo    Manifests   %MANIFESTS%
echo.
echo    AppCenter.csproj now records %VERSION% - commit it with the release.
echo.
echo Smoke-test the installer the way winget will run it:
echo    "%SETUP%" /VERYSILENT
echo    winget validate --manifest "%MANIFESTS%"
echo.
echo Then attach the installer, its .sha256 and the portable zip to a release
echo tagged v%VERSION% - the installer has to be reachable at
echo    %APP_URL%/releases/download/v%VERSION%/AppCenter-%VERSION%-Setup.exe
echo - and open a PR on microsoft/winget-pkgs with the manifest folder. Copies
echo of App Center already out there offer the release from GitHub as soon as
echo it is published, and check the download against the .sha256.
echo.
exit /b 0

rem ----------------------------------------------------------------------------

:find_iscc
rem  winget installs Inno per-machine, but a hand install can be per-user.
set "ISCC="
for %%p in (
  "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
  "%ProgramFiles%\Inno Setup 6\ISCC.exe"
  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
) do if not defined ISCC if exist "%%~p" set "ISCC=%%~p"
if defined ISCC exit /b 0
for /f "delims=" %%p in ('where ISCC.exe 2^>nul') do if not defined ISCC set "ISCC=%%p"
exit /b 0

:render
rem  %1 = manifest suffix; fills the placeholders in installer\winget\template.*
set "SRC=%ROOT%\installer\winget\template.%~1"
set "DST=%MANIFESTS%\%PACKAGE_ID%.%~1"
rem  Publisher and copyright are read straight out of the csproj here, so the
rem  characters never pass through a batch variable.
powershell -NoProfile -Command "$p = ([xml](Get-Content -Raw -Encoding UTF8 -LiteralPath '%PROJ%')).Project.PropertyGroup; $pub = ($p.Company | Where-Object { $_ } | Select-Object -First 1); $cop = ($p.Copyright | Where-Object { $_ } | Select-Object -First 1); $t = Get-Content -Raw -Encoding UTF8 -LiteralPath '%SRC%'; $t = $t -replace '__PACKAGE_ID__','%PACKAGE_ID%' -replace '__VERSION__','%VERSION%' -replace '__SHA256__','%SHA256%' -replace '__PUBLISHER__',$pub -replace '__APP_URL__','%APP_URL%' -replace '__LICENSE__','%LICENSE%' -replace '__COPYRIGHT__',$cop -replace '__DATE__',(Get-Date -Format 'yyyy-MM-dd'); Set-Content -LiteralPath '%DST%' -Value $t -NoNewline -Encoding utf8"
if errorlevel 1 (
  echo ERROR: could not render "%DST%".
  exit /b 1
)
echo    %PACKAGE_ID%.%~1
exit /b 0

:fail
echo.
echo *** release build FAILED ***
exit /b 1
