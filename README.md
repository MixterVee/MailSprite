# MailSprite — Test 9 (all-original visual and audio styles)

MailSprite watches Mailbird notifications using Windows' native Notification Listener API. Gmail/Outlook passwords and other paid services are **not required**.

## New styles
- **Minimal Mail Card (default):** compact, restrained dark alert with clear typography, subtle entrance and persistent unread-*alert* count
- **Envelope Swoop:** an original illustrated sealed envelope glides into a dark notification, then the flap opens before resting
- **Mailbox Flag:** original metal mailbox illustration rises and its orange flag lifts into place
- **Custom animated GIF:** optional personal artwork (from Test 8). The earlier cartoon Butler/Envelope art remains in source for historical reference but is no longer offered as a default style.

Use the system-tray menu **Default animation** to pick a style, or **Test: ...** to preview any of the three. The preview honors **Keep visible until cleared** (click preview or use **Clear preview**).

## Included free sounds
Pick **Notification sound** → **Soft chime**, **Paper flick**, **Clear bell**, or **Off**. These WAV sounds are *generated directly by code* using original waveforms, with no downloaded sounds or paid assets. Picking a sound plays an immediate sample.

**Voice announcements are off by default** for existing users of the basic Windows voice. The prior optional Windows synthetic TTS and user-provided WAV selections remain supported under **Voice announcement**. Piper neural speech is **not bundled** in Test 9; it would require installing a model and checking its license, and we will only consider it if there is a clear need.

## Persistent notifications
When **Keep visible until cleared** is checked, new Mailbird notices update the existing alert and its count. The count is an event count since clearing, **not** a live inbox unread-message count. The alert persists until clicked or **Clear waiting mail alert** is selected. The saved count is restored on restart and the window shifts modestly every 50 seconds; moving pixels cannot guarantee OLED burn-in prevention. Set Windows to turn the display off when idle.

## Build and try
1. Exit the previous MailSprite through the tray icon.
2. Download the Windows x64 ZIP artifact from the [latest successful GitHub Actions build](https://github.com/MixterVee/MailSprite/actions), extract and run MailSprite.exe.
3. Select **Test: Minimal notice**, **Test: Envelope Swoop**, and **Test: Mailbox Flag**. With persistence on, click each to clear it.
4. Choose a **Notification sound** in the tray menu to hear it.
5. Check **Enable Windows notification listener (permission)** if needed, then send a test email to confirm the original, known-working trigger remains intact.

No licensing restrictions for the original vector artwork and synthesized sound designs in Test 9 are imposed by additional asset suppliers.
