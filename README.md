# MailSprite

Windows 11 tray app providing IncrediMail-inspired butler and envelope animations while you keep Mailbird.

## Test 5 — Windows notification listener

Your independent **Notification Logger** app recorded real Mailbird notifications even though the prior window popup probes did not. Accordingly, Test 5 stops using window shapes as an email signal. It instead probes Microsoft's official **UserNotificationListener** API.

### Important Windows packaging limitation

Microsoft documents this API as requiring the **User Notification Listener capability** in the application manifest plus explicit user consent. Our first Test 5 build is still an **unpackaged EXE** to see whether the API is even available in this configuration; **do not assume that permission "Allowed" means it works**. Some unpackaged applications can query notifications but can't subscribe to change events, and others cannot see notifications at all. If the diagnostic report shows no app identity or no notifications, the next step will be a properly signed and installed MSIX package with userNotificationListener capability.

### Testing

1. Fully exit older MailSprite versions from the tray.
2. Download latest GitHub Actions **MailSprite-Windows-x64** ZIP, extract and run.
3. Right-click tray icon → **Enable Windows notification listener (permission)**.
4. If Windows presents a notification access permission request, choose Allow if you're comfortable.
5. Send yourself an email *after* enabling the listener.
6. Right-click MailSprite → **Copy notification listener diagnostic report**; paste the text in chat.

No Gmail or Outlook passwords required. The code reads **notification origin (app name/ID), identifier, and timestamp**, not notification subjects, message bodies or sender addresses. A permissioned listener is inherently capable of accessing all apps' notifications at the OS level; MailSprite deliberately filters to Mailbird and does not save any message content.

Test 5 may need proper packaging before detection works. This release is a diagnostic experiment, **not a claim that email alerts are fixed**.

Official API reference: https://learn.microsoft.com/en-us/windows/uwp/design/shell/tiles-and-notifications/notification-listener
