# CineDock

CineDock is a Windows desktop movie browser that opens a compatible HTTP media directory and plays direct movie links with an embedded native libVLC engine. It is designed for MKV files that browser video elements commonly cannot handle, including multiple audio tracks and embedded subtitles.

## Highlights
- Browses a configured HTTP media library in a Chromium-based interface.
- Plays MKV, MP4, AVI, MOV, WebM, M4V, and TS links with native libVLC.
- Supports multi-audio, subtitle cycling, seeking, playback speed, volume up to 200%, frame stepping, audio/subtitle delay, and video adjustments.
- Uses a custom Windows title bar and a native-window-safe VLC-style dock.
- Fullscreen playback hides the app chrome and controls until mouse movement; double-clicking the video toggles fullscreen.
- Keyboard commands are active only while CineDock is focused, so they do not capture keys in other applications.

## Install
Download `CineDock-Setup-0.1.0.exe` from the GitHub Releases page and run it. The installer requests UAC permission because it installs CineDock for all users under `C:\Program Files\CineDock` and creates Start Menu and desktop shortcuts.

CineDock includes the VLC runtime needed for playback. A separate VLC installation is not required by the packaged app.

## Keyboard shortcuts
| Action | Keys |
| --- | --- |
| Play / pause | `Space` |
| Seek | `Left` / `Right` (10s), `Shift` + arrows (3s), `Ctrl` + arrows (60s) |
| Volume / mute | `Up` / `Down`, `M` |
| Fullscreen / back | `F`, `Esc` |
| Audio / subtitle track | `B`, `V` |
| Frame step | `E` |
| Playback rate | `[` / `]`, `=` for normal |
| Subtitle delay | `G` / `H` |
| Audio delay | `J` / `K` |

## Build from source
Requirements: Windows 10/11 x64, Node.js, .NET 10 SDK, and a 64-bit VLC installation at `C:\Program Files\VideoLAN\VLC` for packaging.

```powershell
npm ci
npm run check
npm run package
```

The Windows NSIS installer is written to `release/`. `npm run package` publishes the self-contained native host, stages the installed VLC runtime, and packages everything into the installer.

## Privacy and network
CineDock contacts only the media-server URL you enter or browse to. It does not provide an account system, telemetry, or a cloud catalog. Use it only with media servers and files you are authorized to access.

## Licensing and notices
CineDock source is licensed under GPL-3.0-or-later. The installer distributes VLC/libVLC components from the VideoLAN project; their notices are preserved in the staged VLC runtime. VLC is GPL-2.0-or-later and libVLC is LGPL-2.1-or-later; see [VideoLAN’s source README](https://code.videolan.org/chub/vlc/-/blob/master/README.md) and `THIRD_PARTY_NOTICES.md`. Content was rephrased for compliance with licensing restrictions.

CineDock is not affiliated with or endorsed by the VideoLAN organization.
