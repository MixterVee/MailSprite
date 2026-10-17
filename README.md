# MailSprite — Test 14

MailSprite uses Windows notifications to detect Mailbird messages without requiring Gmail or Outlook passwords.

## Test 14 changes

- **Protect the beginning of the notification sound:** A 1-second near-silent device warm-up is inserted before the first sound, so slow-to-wake laptop, USB and Bluetooth audio devices have time to start playing. This matches Test 13's 700 ms tail protection for the voice.
- **No voice overlap:** The existing uninterrupted sound -> 160 ms gap -> external WAV voice -> 700 ms tail playback order is unchanged.
- **Previews protected too:** Picking a sound under **Notification sound** now plays it with the same warm-up buffer, so choosing a chime doesn't clip its first notes on a sleepy output device.
- **Other announcement modes:** The notification portion of Windows system-voice announcements receives the same warm-up.
- Existing built-in sounds, user chime, animations, Mailbird notifications and settings are unchanged.

Test the same sound and announcement on both computers. Expect a brief ~1 second delay before hearing the notification. If the first notes are still missing on the second laptop, investigate audio driver enhancements or Bluetooth/HDMI output switching.

## Test 13 changes

- **Device-specific voice clipping fix:** One stereo PCM WAV stream now contains the complete selected notification sound, a 160 ms pause, and the selected external voice WAV. This avoids destroying and reopening the Windows output session between clips.
- **Trailing quiet buffer:** 700 ms of silence follows the complete voice WAV so some Windows audio devices do not truncate its ending.
- **Audio format compatibility:** The supplied voice WAV is decoded/resampled to 44.1 kHz, 16-bit stereo for the combined output; mono voices and stereo chimes are both handled.
- **Fallback:** If a WAV cannot be decoded, MailSprite still attempts the previous playback method. Windows SAPI speech continues to use the existing sequential behavior.
- Everything else, including the five compiled notification sounds, listener, GIF options, and persistent alerts, is unchanged.

### Check

On both computers select the same notification sound and **Voice announcement → My recorded WAV**, then click **Test current selection**. Verify the entire voice begins after the notification sound finishes and the last word is heard. If the second computer still trims the voice, verify it's the exact same WAV file and try disabling that computer's Windows audio enhancements.

## Test 12 changes

- **Two additional built-in notification sounds:**
  - **Slow clanging bell (three dings)**: a 2.7-second metallic bell based on the earlier slow-clang effect
  - **Chime (your recording)**: the actual chime audio supplied by the project owner, embedded into the executable (about 7.7 seconds)
- Both appear directly under **Notification sound** alongside Off, Soft Chime, Paper Flick, and Clear Bell. Neither requires browsing for a sound file.
- The MP3 chime is decoded to WAV in memory on first playback (using NAudio), then cached. All sounds use the existing blocking WAV playback sequence: notification sound, short pause, then optional external WAV voice announcement.
- No changes to automatic Mailbird notification detection, GIF selection, or persistent alerts.

## Test 11 changes

- **Sound, then voice:** Each new-mail alert or **Test current selection** plays the selected built-in notification sound **to completion**, pauses 160 ms, then plays the selected voice announcement (external WAV or Windows voice). The two cannot overlap.
- **Built-in sounds remain selectable:** Soft Chime, Paper Flick, Clear Bell, and Off are still compiled into MailSprite. Selecting one from the sound menu continues to preview that sound only.
- **External WAV announcements remain supported:** Choose your own announcement under **Voice announcement → Choose WAV announcement...**. The voice is optional; it plays by itself if notification sound is Off.
- **Concurrent alerts are serialized:** Alerts arriving while audio is playing queue sound/voice pairs instead of talking over one another.
- No changes to the confirmed Windows notification listener, custom animation GIF playback, or persistent waiting alerts.

To test: select a notification sound and a recorded WAV announcement; choose **Test current selection**. The chime should finish before the recording begins. Repeat with Notification sound set to Off and with Voice announcement set to Off.

## Previous Test 10 changes

- **Single preview action:** The tray menu now has one **Test current selection** entry rather than separate entries for each style. It previews the animation currently checked under **Default animation**, including a user-imported custom GIF.
- **Closed-envelope icon:** The app uses the supplied blue closed-envelope artwork, converted to a transparent Windows multi-size `.ico` (16, 32, 48 pixels). It appears as MailSprite's tray icon and is embedded as the Windows executable icon, instead of the generic blue Information symbol.
- **No changes to notification detection:** The confirmed working Windows listener, persistent waiting-alert mode, alert counter, optional animation GIFs, and built-in sounds stay in place.

## Verify on Windows

1. Fully exit the old MailSprite via its tray menu. Download the `MailSprite-Windows-x64` ZIP from the latest successful [GitHub Actions build](https://github.com/MixterVee/MailSprite/actions) and extract the executable.
2. Start the new `MailSprite.exe` and check the tray for the blue closed envelope.
3. Right-click the envelope and choose **Default animation** → any built-in style or custom animated GIF (if you've imported one).
4. Select **Test current selection**. The selected animation and ordered audio sequence should play; if if **Keep visible until cleared** is enabled, click the preview or choose **Clear preview**.
5. Send a test email to confirm automatic Mailbird notifications still work.

**Windows icon cache:** Explorer sometimes caches icons by executable name and path. If the file or taskbar icon looks unchanged after replacing an old build, first launch the new build from a fresh extracted folder. This does not affect the tray icon, which is loaded from the app resource.

The icon is derived from the image supplied by the project owner for use in MailSprite.
