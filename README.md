# MailSprite

Windows 11 system-tray app restoring IncrediMail-inspired animations while you use Mailbird.

## Test 4 — Mailbird tray notification detector

Test 4 switches from the overly broad 0.2 window-show events to a deliberately conservative **new lower-right popup** watcher.

- A new candidate must be a small, visible notification-like window near a desktop notification corner.
- A candidate must be a secondary window belonging to Mailbird, or a clearly Mailbird-labelled Windows-hosted toast.
- Popup windows already on screen when monitoring starts are ignored.
- Simply opening Mailbird or its normal main window, taskbar icon changes and ordinary tray activity should not trigger an animation.
- Persistent popups trigger only once; alerts are throttled to once every 12 seconds.
- No Gmail/Outlook account access or passwords.
- No email subjects, sender details or message text are logged. Windows may expose notification text to the detection API, but MailSprite only checks for the word "Mailbird", discarding everything else.

### Enable Mailbird's native popup

Mailbird → Menu → Settings → General → **Show tray notifications when receiving a message**.

This is independent from **Show unread count in taskbar & system tray**. If Mailbird only draws an unread badge and displays no actual notification, Test 4 cannot detect that message.

### How to test

1. **Exit** the old MailSprite entirely from the tray.
2. Download the Windows x64 ZIP artifact from the [latest successful build](https://github.com/MixterVee/MailSprite/actions), unzip and run the EXE.
3. Right-click MailSprite's tray icon. **Test: Waving Butler** should still work.
4. Enable **Experimental: watch Mailbird pop-ups**.
5. Leave Mailbird open or minimized. Wait at least 5 seconds for the detector to establish the current-window baseline.
6. Send a new email to a Mailbird inbox. When it arrives, check whether Mailbird displays a **rectangular notification pop-up**, not just its numbered taskbar badge.
7. Choose **Copy Mailbird diagnostic report** and paste that report into your test chat.

The current test is not guaranteed to detect all Mailbird versions or notification styles. If the report shows no popup candidates and only a taskbar badge is visible, a more direct email integration may be required.
