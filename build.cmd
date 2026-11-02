@echo off
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Cannot find .NET Framework 4.8 compiler: %CSC%
  exit /b 1
)
if not exist "vendor\MySqlConnector.dll" (
  echo Missing vendor\MySqlConnector.dll
  exit /b 1
)

set "OUT=curl2url.build.exe"
if exist "%OUT%" del /q "%OUT%"

"%CSC%" -target:winexe -codepage:65001 -optimize+ -out:"%OUT%" ^
  -reference:System.Windows.Forms.dll -reference:System.Drawing.dll ^
  -reference:System.Security.dll -reference:System.Data.dll ^
  -reference:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Web.Extensions.dll" ^
  -reference:vendor\MySqlConnector.dll ^
  -reference:vendor\System.Memory.dll -reference:vendor\System.Buffers.dll ^
  -reference:vendor\System.Threading.Tasks.Extensions.dll ^
  -reference:vendor\System.Runtime.CompilerServices.Unsafe.dll ^
  -reference:vendor\System.Numerics.Vectors.dll ^
  -resource:vendor\MySqlConnector.dll,MySqlConnector.dll ^
  -resource:vendor\System.Memory.dll,System.Memory.dll ^
  -resource:vendor\System.Buffers.dll,System.Buffers.dll ^
  -resource:vendor\System.Threading.Tasks.Extensions.dll,System.Threading.Tasks.Extensions.dll ^
  -resource:vendor\System.Runtime.CompilerServices.Unsafe.dll,System.Runtime.CompilerServices.Unsafe.dll ^
  -resource:vendor\System.Numerics.Vectors.dll,System.Numerics.Vectors.dll ^
  -win32icon:curl2url.ico -win32manifest:curl2url.manifest ^
  -recurse:src\*.cs Curl2UrlApp.cs
if errorlevel 1 (
  echo.
  echo Build failed. Existing curl2url.exe was not modified.
  exit /b 1
)

echo.
echo Running selftest...
"%~dp0%OUT%" --selftest
if errorlevel 1 (
  echo.
  echo Selftest failed. Existing curl2url.exe was not modified.
  exit /b 1
)

move /Y "%OUT%" curl2url.exe >nul
if errorlevel 1 (
  echo.
  echo Cannot overwrite curl2url.exe. Close the running program and retry.
  exit /b 1
)

echo.
echo Build complete: curl2url.exe
endlocal
