@echo off
setlocal
pushd "%~dp0"
set "PBG_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%PBG_CSC%" set "PBG_CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%PBG_CSC%" goto compiler_missing
"%PBG_CSC%" /nologo /langversion:5 /codepage:65001 /target:winexe /platform:anycpu /optimize+ /win32icon:assets\PbgStudio.ico /win32manifest:assets\app.manifest /out:PbgStudio.exe /r:System.Core.dll /r:System.Xml.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\*.cs
if errorlevel 1 goto failed
echo Build succeeded: PbgStudio.exe
popd
pause
exit /b 0
:compiler_missing
echo The .NET Framework compiler was not found. Use Windows with .NET Framework 4.7.2 or newer.
goto failed
:failed
echo Build failed. Close PbgStudio before building and review the error above.
popd
pause
exit /b 1
