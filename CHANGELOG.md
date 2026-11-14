# Changelog

All notable changes to str-amp go here!

Format: [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)

## [v2.0.1] — Nov. 13, 2026

### Added:
- Theming system
- - Light Theme
- - Aero Theme
- Spotify Integration (Playlist Import)

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