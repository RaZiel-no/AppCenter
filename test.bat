@echo off
setlocal

rem  App Center - run the tests.
rem
rem    test.bat              everything
rem    test.bat Batch        only tests whose full name contains "Batch"
rem
rem  The suite is unit tests over the parsing, the operation state and the
rem  catalogue. Nothing here starts winget or touches the machine, so it is
rem  always safe to run.

set "PROJ=%~dp0tests\AppCenter.Tests\AppCenter.Tests.csproj"

if "%~1"=="" (
  dotnet test "%PROJ%" --nologo
) else (
  dotnet test "%PROJ%" --nologo --filter "FullyQualifiedName~%~1"
)

if errorlevel 1 (
  echo.
  echo *** tests FAILED ***
  exit /b 1
)

exit /b 0
