# MailSprite — Test 10

MailSprite uses Windows notifications to detect Mailbird messages without requiring Gmail or Outlook passwords.

## Test 10 changes

- **Single preview action:** The tray menu now has one **Test current selection** entry rather than separate entries for each style. It previews the animation currently checked under **Default animation**, including a user-imported custom GIF.
- **Closed-envelope icon:** The app uses the supplied blue closed-envelope artwork, converted to a transparent Windows multi-size `.ico` (16, 32, 48 pixels). It appears as MailSprite's tray icon and is embedded as the Windows executable icon, instead of the generic blue Information symbol.
- **No changes to notification detection:** The confirmed working Windows listener, persistent waiting-alert mode, alert counter, optional animation GIFs, and built-in sounds stay in place.

## Verify on Windows

1. Fully exit the old MailSprite via its tray menu. Download the `MailSprite-Windows-x64` ZIP from the latest successful [GitHub Actions build](https://github.com/MixterVee/MailSprite/actions) and extract the executable.
2. Start the new `MailSprite.exe` and check the tray for the blue closed envelope.
3. Right-click the envelope and choose **Default animation** → any built-in style or custom animated GIF (if you've imported one).
4. Select **Test current selection**. Only the selected animation should appear; if **Keep visible until cleared** is enabled, click the preview or choose **Clear preview**.
5. Send a test email to confirm automatic Mailbird notifications still work.

**Windows icon cache:** Explorer sometimes caches icons by executable name and path. If the file or taskbar icon looks unchanged after replacing an old build, first launch the new build from a fresh extracted folder. This does not affect the tray icon, which is loaded from the app resource.

The icon is derived from the image supplied by the project owner for use in MailSprite.
