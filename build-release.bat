@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   RemoteHub 1.4.1  Release Builder
echo ============================================
echo.

rem --- 0) main executable -------------------------------------------------
call build.bat anycpu silent
if errorlevel 1 goto :fail

rem --- 1) portable / green edition ---------------------------------------
echo.
echo [1/3] Building portable package ...
if not exist "dist" mkdir "dist"
if exist "dist\Portable" rmdir /s /q "dist\Portable"
mkdir "dist\Portable\RemoteHub"
mkdir "dist\Portable\RemoteHub\Data"
copy /y "RemoteHub.exe" "dist\Portable\RemoteHub\" >nul
copy /y "UserGuide.txt" "dist\Portable\RemoteHub\" >nul
>  "dist\Portable\RemoteHub\portable.flag" echo RemoteHub Portable Edition
>> "dist\Portable\RemoteHub\portable.flag" echo Connections are stored in .\Data\connections.xml
>> "dist\Portable\RemoteHub\portable.flag" echo Delete this file to switch to the installed-mode config path.
if exist "dist\RemoteHub-1.4.1-Portable.zip" del /f /q "dist\RemoteHub-1.4.1-Portable.zip"
powershell -NoProfile -Command "Compress-Archive -Path 'dist\Portable\RemoteHub' -DestinationPath 'dist\RemoteHub-1.4.1-Portable.zip' -Force"
if errorlevel 1 goto :fail
echo       OK: dist\RemoteHub-1.4.1-Portable.zip

rem --- 2) installer -------------------------------------------------------
echo.
echo [2/3] Building installer with Inno Setup ...
set "ISCC=C:\Program Files\Inno Setup 7\ISCC.exe"
if not exist "%ISCC%" set "ISCC=C:\Program Files (x86)\Inno Setup 7\ISCC.exe"
if not exist "%ISCC%" set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=C:\Program Files\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
  echo [WARN] Inno Setup not found - installer skipped.
  goto :done
)
"%ISCC%" "RemoteHub.iss"
if errorlevel 1 goto :fail
echo       OK: dist\RemoteHub-1.4.1-Setup.exe

:done
echo.
echo [3/3] Release artifacts in dist\ :
dir /b "dist\*.exe" "dist\*.zip" 2>nul
echo.
echo Done.
if /i not "%~1"=="silent" pause
exit /b 0

:fail
echo.
echo [ERROR] Release build failed.
if /i not "%~1"=="silent" pause
exit /b 1
