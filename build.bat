@echo off
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0LaptopTemps.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll /r:System.Web.Extensions.dll ^
  /r:"C:\Windows\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Management.Infrastructure\v4.0_1.0.0.0__31bf3856ad364e35\Microsoft.Management.Infrastructure.dll" ^
  "%~dp0Program.cs"
if errorlevel 1 exit /b 1
echo Built %~dp0LaptopTemps.exe
