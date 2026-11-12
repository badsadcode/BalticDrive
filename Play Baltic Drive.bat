@echo off
rem Runs Baltic Drive from the project with Godot 4.7.2 .NET (builds the C# first if needed).
rem Start somewhere else: Play Baltic Drive.bat Radom   (any town in the map)
set GODOT=F:\Codex\Project9a\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe
set PLACE=%~1
if "%PLACE%"=="" set PLACE=Warszawa
cd /d "%~dp0"
if not exist "%GODOT%" (
  echo Godot was not found at %GODOT%
  echo Edit the GODOT line at the top of this file.
  pause
  exit /b 1
)
dotnet build BalticDrive.csproj -nologo -v q
start "" "%GODOT%" --path "%~dp0." -- --place=%PLACE%
