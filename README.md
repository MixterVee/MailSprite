# MailSprite

MailSprite is an experimental Windows 11 tray application that brings back playful IncrediMail-style alerts while keeping Mailbird.

## Test 3 — version 0.3

**Important safety fix:** Versions 0.1 and 0.2 could accidentally treat ordinary Mailbird windows (including opening the app) as new email notifications. Version 0.3 **disables those event-based animation triggers**, and migrates existing settings to turn the old watcher OFF. You can still run the butler and envelope animations from the tray menu.

The next step is to establish whether the small unread counter displayed by Mailbird is exposed through Windows accessibility APIs. It may be a painted image and therefore invisible to these APIs.

### Test 3 instructions

1. Completely **Exit MailSprite** version 0.2 from the system tray.
2. Download Test 3 from the latest successful [GitHub Actions build](https://github.com/MixterVee/MailSprite/actions).
3. Unzip and run MailSprite.exe.
4. Right-click MailSprite tray icon and choose **Probe unread badge (copies report)**. Paste the copied report into the chat.
5. (Optional) If desired, enable **Experimental: watch Mailbird pop-ups** only to gather an additional diagnostics report. This mode will no longer show animations automatically; the watcher now only logs window events.
6. You can still preview the butler or envelope using the Test menu items.

**This build prioritizes stopping false alerts and investigating the correct trigger, not claiming working email detection.** MailSprite never requests Gmail/Outlook credentials and does not collect email subjects or sender/account labels in probe reports.

For a reliable solution, the future notifier may need a dedicated provider integration or an app-supported notification event.