@echo off
rem Rebuilds SynthAnalyser.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio or downloads needed.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe from the .NET Framework 4.x.
  exit /b 1
)
cd /d "%~dp0"
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:..\SynthAnalyser.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  /resource:src\VitalTemplate.json,SynthAnalyser.VitalTemplate.json src\*.cs
if errorlevel 1 (
  echo Build failed.
  exit /b 1
)
echo Built ..\SynthAnalyser.exe
