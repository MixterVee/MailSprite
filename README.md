# MailSprite

Windows tray app restoring playful IncrediMail-inspired notifications without replacing Mailbird.

## Test 6 — animated characters and positioning

**The user confirmed that Test 5 automatically played the butler animation precisely when the Mailbird Windows notification arrived.** The working official Windows notification listener is retained, unchanged, in Test 6.

### Changes

- Butler vector artwork refreshed: expressive face, curled mustache, bow tie, waistcoat buttons, outlined white gloves and a gold-banded top hat.
- Smoother spring-like entrance, independently waving arm, hat tip, head nod and eye blink.
- Flying envelope character with independently fluttering wings and a lively entrance.
- Speech bubble pop animation, golden sparkles and gradual fade-away.
- **Animation position** tray menu with Left of Windows notifications (default), Bottom left, Top left, Top right.
- Default position reserves horizontal space for a typical Mailbird/Windows notification on the bottom-right. Narrow desktops fall back to top-left to prevent overlap.
- Click a character to dismiss with a quick fade.
- Previous duration, sound, voice and Windows startup settings remain available.

### How to test

1. Exit your previous MailSprite completely via the tray icon.
2. From [GitHub Actions](https://github.com/MixterVee/MailSprite/actions), download the latest successful **MailSprite-Windows-x64** artifact.
3. Extract and run MailSprite.exe.
4. Use **Test: Waving Butler** and **Test: Flying Envelope** to preview changes.
5. Use **Animation position** to choose where the character appears.
6. Select **Enable Windows notification listener (permission)** and send a test email to confirm detection continues to work.

The notification listener remains a manual enable action after launch, as in Test 5. MailSprite does not require Gmail or Outlook credentials, and does not inspect message text, senders or subjects. Existing art is original vector work rather than imported copyrighted IncrediMail assets.
