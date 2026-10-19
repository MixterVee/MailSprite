@echo off
setlocal
set "HELPER=%LOCALAPPDATA%\eMClientTrayStarter\eMClientTrayStarter.exe"
if not exist "%HELPER%" (
  echo The helper is not installed in the usual location.
  pause
  exit /b 1
)
"%HELPER%" --uninstall
