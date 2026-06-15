@echo off
setlocal
set "PORT=%EXPCTL_PORT%"
if "%PORT%"=="" set "PORT=9999"
if "%~1"=="" (
  echo usage: expctl.bat ^<command^> [args...]   ^(try: expctl.bat help^)
  exit /b 1
)
if /I "%~1"=="forward" (
  adb forward tcp:%PORT% tcp:%PORT%
  echo forwarded localhost:%PORT% -^> device:%PORT%
  exit /b 0
)
curl -s "http://localhost:%PORT%/" -d "%*"
