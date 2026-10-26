@echo off
rem Compiles termvis.cs in memory and runs it (no exe is written). Extra arguments are passed through, e.g. run.bat --mic
cd /d "%~dp0"
rem Settings (termvis.ini) are saved in this folder.
set "TERMVIS_HOME=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "& { Add-Type -TypeDefinition ([IO.File]::ReadAllText('%~dp0termvis.cs')) -Language CSharp; exit [TermVis.Program]::Main([string[]]$args) }" %*
