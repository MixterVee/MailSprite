# MailSprite

Windows tray utility bringing back playful IncrediMail-style email notifications while you keep Mailbird.

## Version 0.2 - diagnostic build

- Preview **Waving Butler** and **Flying Envelope** via tray menu.
- Optional beep and Windows speech, adjustable duration, optional autostart.
- Experimental Mailbird detection using Windows native show/alert hooks plus UI Automation window-open events.
- **Copy Mailbird diagnostic report** produces anonymized event metadata (window process/class, event counts); it does not collect message bodies, senders, or subjects.

## How to test

1. Download the latest Windows x64 build artifact from the **Actions** page. Extract and run `MailSprite.exe`.
2. Right-click the system tray icon and verify **Test: Waving Butler** works.
3. Turn on **Experimental: watch Mailbird pop-ups** (it's OFF by default), and leave Mailbird running with its mail pop-up notifications ON.
4. Send a test email to your Gmail or Outlook account. Wait for Mailbird to display its notification.
5. Right-click MailSprite's icon → **Copy Mailbird diagnostic report** and paste the text into the ChatGPT conversation.

This is diagnostics-first: even with both Windows event-hook types, certain Mailbird notification implementations may not expose usable events. If there are no Mailbird events, we'll need a different integration method. The app does **not** connect to Gmail or Outlook and never asks for passwords.

## Build

`dotnet publish MailSprite/MailSprite.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false`
