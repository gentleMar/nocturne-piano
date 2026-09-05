@echo off
cd /d "%~dp0"
start /wait "" App\Nocturne.exe --self-test %*
type App\test-results.txt
pause
