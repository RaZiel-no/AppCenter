@echo off
setlocal

rem  App Center - run the tests.
rem
rem    test.bat              everything that runs without winget
rem    test.bat Batch        only tests whose full name contains "Batch"
rem    test.bat machine      the checks against the real winget on this machine
rem
rem  The suite is unit tests over the parsing, the operation state and the
rem  catalogue. Nothing in it starts winget or touches the machine, so it is
rem  always safe to run. The tests marked Machine are the exception - they
rem  read the real sources through both the API and winget.exe and compare
rem  the answers - and run only when asked for by name.

set "PROJ=%~dp0tests\AppCenter.Tests\AppCenter.Tests.csproj"

if "%~1"=="" (
  dotnet test "%PROJ%" --nologo --filter "Category!=Machine"
) else if /I "%~1"=="machine" (
  dotnet test "%PROJ%" --nologo --filter "Category=Machine" --logger "console;verbosity=normal"
) else (
  dotnet test "%PROJ%" --nologo --filter "FullyQualifiedName~%~1&Category!=Machine"
)

if errorlevel 1 (
  echo.
  echo *** tests FAILED ***
  exit /b 1
)

exit /b 0
