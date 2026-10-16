# MailSprite

MailSprite uses the **Windows Notification Listener API** to detect Mailbird arrivals and show an optional IncrediMail-inspired desktop alert. No Gmail/Outlook sign-in is needed.

## Test 7 — persistent mail alerts and cleaner visuals

The previous cartoon-style characters felt cheesy. Test 7 makes a new **Elegant Mail Notice** the default: a compact dark-blue card, a small envelope glyph, restrained fade-and-slide entrance, and no cartoon sparkles or exaggerated bounce. Classic butler and flying-envelope previews remain available.

### Stay visible until cleared

The new **Keep visible until cleared** option is ON by default. When a new Mailbird notification arrives:

- MailSprite displays a compact card that persists indefinitely while the app remains running.
- Additional detected notifications update the existing card, rather than opening a stack of windows.
- Click the alert or use tray menu **Clear waiting mail alert** to dismiss.
- Counts represent **MailSprite alerts since the last clear**, NOT the live unread count of Gmail/Outlook/Mailbird.
- The count is stored in %APPDATA%/MailSprite/settings.json; a pending card can be restored after restarting MailSprite.
- With the option OFF, the alert auto-hides according to the existing timed-duration setting.

### Motion and burn-in considerations

In persistent mode, the entire alert window drifts to another location by around 10–25 pixels every 50 seconds, using a slow 2.4-second move. This helps avoid a *completely static* overlay but **does not guarantee prevention of OLED burn-in**. Use Windows display-off/sleep settings when away for extended periods; you will still see the reminder when the display comes back on.

### Notification monitoring

The Test 5/6 Windows notification listener is preserved. After granting access once, MailSprite tries to restart monitoring on its next launch automatically. Stop monitoring through the tray menu to disable this auto-start. To run at Windows login, separately enable **Start with Windows**.

### Test

1. Exit MailSprite 0.6.
2. Download the latest successful Windows x64 ZIP from [GitHub Actions](https://github.com/MixterVee/MailSprite/actions) and run MailSprite.exe.
3. In the tray menu select **Default animation → Elegant mail notice**.
4. Enable **Keep visible until cleared** and preview it with **Test: Elegant notice** (preview always auto-closes after 5 seconds).
5. Ensure **Enable Windows notification listener (permission)** is active and send an email.
6. Confirm the real incoming-mail card remains until you click it, then send a second email to ensure it appears again.

The app never retrieves message bodies or subjects. Windows may show notification details to authorized listeners, but MailSprite only matches their originating app.

If you prefer real animated characters instead of the cleaner card, we should use professionally illustrated animation assets in a later build instead of crude procedural vector drawings.
