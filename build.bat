@echo off
setlocal
set "PROJ=%~dp0AppCenter.csproj"

echo === Debug ===
dotnet build "%PROJ%" -c Debug
if errorlevel 1 exit /b 1

echo.
echo === Release ===
dotnet build "%PROJ%" -c Release
if errorlevel 1 exit /b 1

echo.
echo Debug:   %~dp0bin\Debug\net10.0-windows\AppCenter.exe
echo Release: %~dp0bin\Release\net10.0-windows\AppCenter.exe
