@echo off
rem BeBoosted launcher. From the repo root:
rem   .\bb            start the desktop app  [Debug]
rem   .\bb release    start the desktop app  [Release]
setlocal
set "PROJ=%~dp0src\BeBoosted.Desktop\BeBoosted.Desktop.csproj"
if /i "%~1"=="release" (
  echo Starting BeBoosted [Release] ...
  dotnet run --project "%PROJ%" -c Release
) else (
  echo Starting BeBoosted [Debug] ...
  dotnet run --project "%PROJ%" -c Debug
)
