eM Client Tray Starter for Windows 11
=====================================

Designed for the Microsoft Store eM Client installation.
No administrator rights, WindowsApps edits, or exact version-specific paths required.

SET UP (ONCE)
1. In eM Client > Menu > Settings > General, ENABLE both:
   [x] Run on system startup
   [x] Minimize application to tray
   You can also leave Close application to tray checked.
2. Extract the GitHub Actions ZIP.
3. Double-click eMClientTrayStarter.exe. Click Yes when asked to install.
   It copies itself into your user folder and adds a per-user Windows startup entry.
4. Sign out/in or restart Windows to verify. There is no need to keep the ZIP.

WHAT IT DOES
- At sign-in the helper waits up to 3 minutes for eM Client's MailClient.exe window.
- After its main window appears, it sends a normal Windows minimize command ONCE.
- It then exits; it DOES NOT keep monitoring your activity.
- eM Client's own "Minimize application to tray" must be enabled.
- A short diagnostic log is saved to:
  %LOCALAPPDATA%\eMClientTrayStarter\starter.log

TO REMOVE
Run Uninstall.cmd from the extracted ZIP.
The startup registry entry is removed; you can delete:
  %LOCALAPPDATA%\eMClientTrayStarter

LIMITATIONS
- This helper does not start eM Client itself. Leave eM Client's own
  "Run on system startup" enabled.
- If eM Client ignores normal Windows minimize messages or changes its
  main process name in a future release, it may remain visible.
- SmartScreen may warn about this unsigned personal-build executable.
