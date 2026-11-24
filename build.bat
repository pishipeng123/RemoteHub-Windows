@echo off
setlocal
cd /d "%~dp0"

rem Build RemoteHub 1.4.0 with the .NET Framework 4.x compiler shipped with Windows.
rem No Visual Studio / Windows SDK / NuGet / internet access required.
rem Usage: build.bat                 -> AnyCPU (recommended on 64-bit Windows)
rem        build.bat x86             -> 32-bit (use if the 64-bit RDP control is missing)
rem        build.bat anycpu silent   -> no pause, for automated release builds

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] .NET Framework 4.x compiler not found.
  echo         Install .NET Framework 4.x, or build with Visual Studio.
  if /i not "%~2"=="silent" pause
  exit /b 1
)

if not exist "RemoteHub.ico" (
  echo Generating application icon ...
  powershell -NoProfile -ExecutionPolicy Bypass -File "make-icon.ps1"
)
if not exist "RemoteHub.ico" (
  echo [ERROR] RemoteHub.ico is missing and could not be generated.
  if /i not "%~2"=="silent" pause
  exit /b 1
)

set "PLATFORM=anycpu"
if /i "%~1"=="x86" set "PLATFORM=x86"

rem Read the version straight out of AssemblyInfo.cs so the banner can never
rem drift away from what actually got compiled.
set "APPVER=unknown"
for /f "tokens=2 delims=()" %%v in ('findstr /c:"AssemblyFileVersion" AssemblyInfo.cs') do set "APPVER=%%~v"

echo Compiler : %CSC%
echo Platform : %PLATFORM%
echo Building RemoteHub %APPVER% ...

"%CSC%" /nologo /target:winexe /platform:%PLATFORM% /optimize+ /codepage:65001 ^
  /win32manifest:app.manifest /win32icon:RemoteHub.ico ^
  /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll ^
  /r:System.Drawing.dll /r:System.Xml.dll /r:System.Security.dll ^
  /out:RemoteHub.exe ^
  AssemblyInfo.cs Program.cs UiTheme.cs NetProbe.cs Vault.cs LanScanDialog.cs SessionGrid.cs RdpHost.cs ConnectionStore.cs Dialogs.cs FileTransferDialog.cs SessionHistory.cs MainForm.cs

if errorlevel 1 (
  echo.
  echo [ERROR] Build failed. Read the compiler messages above.
  if /i not "%~2"=="silent" pause
  exit /b 1
)

echo.
echo [OK] Built: %~dp0RemoteHub.exe
echo      Version %APPVER% with icon + manifest embedded.
echo      Double-click it, or run:  RemoteHub.exe --selftest
if /i not "%~2"=="silent" pause
