@echo off
rem Builds ClaudeQuotaWidget.exe with the C# compiler that ships with Windows (.NET Framework 4.x). No installs needed.
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe from .NET Framework 4.x.
  pause
  exit /b 1
)
taskkill /im ClaudeQuotaWidget.exe /f >nul 2>&1
"%CSC%" /nologo /optimize+ /target:winexe /platform:anycpu ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll ^
  /out:ClaudeQuotaWidget.exe ClaudeQuotaWidget.cs
if errorlevel 1 (
  echo.
  echo Build failed.
  pause
  exit /b 1
)
echo Built ClaudeQuotaWidget.exe - starting it...
start "" "%~dp0ClaudeQuotaWidget.exe"