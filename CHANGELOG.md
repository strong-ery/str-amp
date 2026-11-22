# Changelog

All notable changes to str-amp go here!

Format: [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)

## [v2.0.2] — Nov. 21, 2026 -> ???
Shinier!

### Added:
- Linux support (x64): PulseAudio/PipeWire playback through ffmpeg, the same FxSound-based effects, MPRIS media keys and desktop media controls, Discord Rich Presence, application-menu launcher, and in-app updates

### Changed:
- Various memory and other GC optimizations (700mb -> ~300mb)

## [v2.0.1] — Nov. 13, 2026 -> Nov. 18, 2026
Many improvements!

### Added:
- A .lrc editor for songs that don't have lyrics on LRCLIB (or if you want to sync them!)
- Theming system (+ Light Theme, + Aero Theme)
- Spotify integration (Playlist Import)
- Status toasts
- Warning when ffmpeg isn't present (used for filetypes incompatible with WASAPI)
- Lightweight markdown renderer for release notes. ( Like these ones :D ) - Issue 7
- Playlist icons + the supporting features
- A "check for updates" button

### Changed:
- Fullscreen mode no longer extends past the screen bounds
- Fullscreen mode now hides elements after X seconds (seamless fullscreen)
- Improved stramp dropdown menu (follows theme better, no weird splitting)
- Fixed various UI bugs (overlap, scaling issues, and reactive-resize for forced cramped states)
- Made audio normalization atomic (cannot start loud) - Bug 6
- Improved playlist's and their associated UI/UX
- Improved song delete popup UI
- Improved many parts of the app to allow for libraries upwards and beyond 100,000 songs
- Github preview release notes render as plaintext

## [v2.0.0] — Nov. 13, 2026
Hello Everyone! We are excited to share the first official release of **str-amp** v2.0.0!

### Added:
- Added a listening stats panel ( `stramp -> View Stats` )
- Added smooth scrolling to all panels
- An auto-updater service
- An install process/setup wizard

### Changed:
- The listening stats service now only counts a "play" as `timeListened > 33%` per song.
- Improved toasts
- No longer builds into hundreds of DLL's

### Tidying:
- Cleaned up the codebase a fair bit.
- Gave the repo the same treatment.
- Deleted the legacy python app.
- Split classes and methods to follow a 256/64 split.
- Wrote a proper readme.