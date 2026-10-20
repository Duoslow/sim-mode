@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32icon:SimMode.ico /out:SimMode.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll SimMode.cs
