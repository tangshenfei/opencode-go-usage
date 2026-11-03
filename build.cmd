@echo off
setlocal

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo csc.exe not found. .NET Framework 4.x is required.
  exit /b 1
)

"%CSC%" /nologo /target:winexe /optimize+ /out:OpenCodeGoUsage.exe src\TrayApp.cs /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Net.Http.dll /resource:assets\logo.png,OpenCodeGoUsage.logo.png /win32icon:assets\app.ico

if errorlevel 1 (
  echo Build failed.
  exit /b 1
)

echo Built: OpenCodeGoUsage.exe
