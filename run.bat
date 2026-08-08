@echo off
setlocal

rem  App Center - build and launch.
rem
rem    run.bat            Debug
rem    run.bat release    Release
rem
rem  Deliberately not "dotnet run": it owns this console for as long as the app
rem  lives, so the window sits there until App Center is closed - and closing the
rem  window first takes the app down with it. Building here keeps failures
rem  readable, then start hands the exe off as its own process and this window
rem  goes away.

set "PROJ=%~dp0AppCenter.csproj"

set "CFG=Debug"
if /i "%~1"=="release" set "CFG=Release"

dotnet build "%PROJ%" -c %CFG% --nologo -v minimal
if errorlevel 1 (
  echo.
  echo *** build FAILED ***
  pause
  exit /b 1
)

set "EXE=%~dp0bin\%CFG%\net10.0-windows\AppCenter.exe"
if not exist "%EXE%" (
  echo.
  echo ERROR: the build succeeded but "%EXE%" is not there.
  pause
  exit /b 1
)

start "" "%EXE%"
exit /b 0
