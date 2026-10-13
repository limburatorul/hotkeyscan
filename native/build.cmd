@echo off
rem Builds the owner-finding hook: hook64.dll, hook32.dll and host32.exe into native\bin.
rem Needs the MSVC build tools (x64 and x86). Called by HotkeyScan.csproj before each build.
setlocal
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set VS=%%i
if not defined VS (echo MSVC build tools not found & exit /b 1)
rem vcvarsall looks vswhere up on PATH and complains when it is not there.
set PATH=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer;%PATH%
cd /d "%~dp0"
if not exist bin mkdir bin
set FLAGS=/nologo /O1 /W4 /GS /MT /DUNICODE /D_UNICODE

call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" x64 >nul || exit /b 1
cl %FLAGS% /LD hook.c /Fobin\hook64.obj /Febin\hook64.dll /link user32.lib || exit /b 1

call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" x86 >nul || exit /b 1
cl %FLAGS% /LD hook.c /Fobin\hook32.obj /Febin\hook32.dll /link user32.lib || exit /b 1
cl %FLAGS% host32.c /Fobin\host32.obj /Febin\host32.exe /link /SUBSYSTEM:WINDOWS user32.lib || exit /b 1
del bin\*.obj bin\*.lib bin\*.exp 2>nul
exit /b 0
