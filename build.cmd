@echo off
cd /d "%~dp0"
"C:\Program Files\dotnet\dotnet.exe" publish src\Nocturne.csproj -c Release -o App --nologo
pause
