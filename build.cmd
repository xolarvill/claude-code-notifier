@echo off
rem Builds notify-taskbar.exe.
rem
rem csc.exe is part of .NET Framework, which ships with Windows -- there is no
rem SDK, workload, or NuGet package to install.

setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo ERROR: csc.exe not found. Is .NET Framework 4.x installed? 1>&2
    exit /b 1
)

"%CSC%" -nologo -target:exe -optimize+ -out:notify-taskbar.exe "%~dp0notify-taskbar.cs"
if errorlevel 1 (
    echo ERROR: build failed. 1>&2
    exit /b 1
)

echo Built notify-taskbar.exe
