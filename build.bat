@echo off
rem Builds termvis.exe using the C# compiler that ships with Windows (.NET Framework 4).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /out:termvis.exe termvis.cs
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
echo Built termvis.exe
