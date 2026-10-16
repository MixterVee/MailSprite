# MailSprite

Windows tray utility using the working **Windows Notification Listener** to display your own animated or minimal alerts for Mailbird. It does not connect to Gmail or Outlook.

## Test 8: Fixed preview and custom artwork/voice options

- **Previews now honor Keep visible until cleared.** A tray-menu test remains on screen until you click it or choose **Clear preview**. With the persistent setting off, the preview times out after five seconds. Testing never creates a real waiting-mail count.
- **Minimal alert** is the new default: compact, dark Windows-style card with a restrained entrance, instead of the experimental cartoon characters.
- **Choose custom animated GIF...** lets you use a locally saved GIF with transparency and its own frame timing (up to 35 MB). You can select **Custom animated GIF** as your default. Classic Butler/Envelope are still available for comparison.
- **Voice announcement** options: Off, Windows system voice, or **My recorded WAV**. Choose **Choose WAV announcement...** and select a WAV containing a human-recorded phrase to avoid synthetic speech. The announcement file stays local.
- **Keep visible until cleared** still works for actual email and restores waiting alerts after restarting; a saved alert count is not an inbox unread count.
- Slow drift is retained in persistent mode. OLED burn-in can still occur; turning the screen off while idle is safer.

### Try it
1. Exit the old MailSprite through its tray menu. Download the latest successful [GitHub Actions Windows x64 artifact](https://github.com/MixterVee/MailSprite/actions).
2. Extract and launch MailSprite.exe.
3. Enable **Keep visible until cleared**, then run **Test: Minimal notice**. It should stay until clicked or **Clear preview** is selected.
4. Optionally select **Choose custom animated GIF...** to import professional character artwork, and **Voice announcement → Choose WAV announcement...** for a natural recorded voice.
5. Send a test email to confirm the working Windows notification listener still triggers the real, persistent alert.

The app uses the [WpfAnimatedGif library](https://www.nuget.org/packages/WpfAnimatedGif/) for GIF playback. Only use art/audio files you have rights to use.
