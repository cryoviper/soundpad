```markdown
# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/)

## [0.4.0]
### Added
- Voice Changer: 12 live mic voices (Deep, Monster, Chipmunk, Kid, Robot, Alien, Radio, Megaphone, Cave, Stadium, Demon), fine-tune pitch, "Hear myself" in headphones, global on/off hotkey (F8), ON badge in sidebar
- Mini player bar at the bottom on every tab: play/pause, replay, stop, seek, loop, your volume, jump to source
- Sound tiles: play icon on hover, double-click to play, smooth hover animation

## [0.3.0]
### Added
- YouTube: drag-to-seek bar, "Cut a clip" (mark start/end while playing, preview), Save as MP3 (trimmed) to a folder + auto-add to board, recently played list, Hindi meme quick-searches
- Text to Speech rebuilt: natural Microsoft neural voices, Hindi (Swara / Madhur) + 100+ languages, language + voice picker, speed/pitch, recent phrases, Ctrl+Enter to speak; eSpeak kept as offline backup
- Per-sound hotkeys (press once to play, again to stop), right-click menu on sounds, search inside a board, drag & drop files or folders, M4A/AAC/FLAC/WMA/AIFF support
- Mixer: separate "friends hear" and "you hear" volume, mic volume + mute
- Mic cleanup for games: noise gate + rumble filter with a live level meter
- VB-Cable help card with a button to open Windows sound settings

### Fixed
- Noisy, too-loud mic on laptops with mic arrays (4-channel downmix bug overran the audio buffer)
- Mic delay slowly growing / "buffer full" errors in long sessions (mic buffer now capped at ~150 ms)
- Everything now mixes at 48 kHz (what VB-Cable, Discord and games use) - less resampling, less crackle
- Settings could be overwritten by an old copy (shared settings object)

## [0.2.2]
### Fixed
- "Speaker output error: Source array type cannot be assigned..." when playing YouTube clips
- Version label no longer shows the long build hash

## [0.2.1]
### Fixed
- YouTube clips getting stuck at 0:00 with no sound: audio is now pulled into memory first (still never saved to disk), then played
- Clear error message instead of a silent hang if YouTube or the audio device fails
- Broken square icons (emoji) replaced with proper vector icons

### Changed
- Full UI redesign: new dark theme with pink accent, cleaner sidebar, sound tiles, YouTube player card, quick-search chips, proper status bar
- Selected-sound panel only shows on the Sounds tab
- Recently played YouTube clips are kept in memory so they replay instantly

## [0.2.0] - Boobies Soundpad
### Added
- YouTube Live tab: search YouTube (or paste a link) and stream audio live into the virtual mic + speakers, no downloads
- Pin YouTube results to a soundboard with optional start/end times; hotkeys, volume, EQ and pitch work on them
- Backup YouTube engine (yt-dlp, fetched on demand) if the main one breaks
- GitHub Actions build + build.bat

### Fixed
- Text-to-Speech no longer plays choppy (each output now has its own reader)
- Loop toggle works even with no sound selected
- Log file no longer written to the Desktop

### Changed
- New name, logo and default icon; data folder is now %AppData%\BoobiesSoundpad

## [Unreleased]
### Added
- VB Cable driver configuration with app
- Sound Library System
- Microphone and playback devices config

### Fixed
- Performance and UI optimizations
- Fix sound Icon bug

---

## [0.1.2] - 2025-05-03
### Added
- Add Splash Window
- Optimize Startup
- Enhanced Sound Library
### Fixed
- Fix default devices bug

---

## [0.1.3] - 2025-05-03
### Added
- Switch from the user being able to hear their own sounds from listening device of the Cable OUTPUT to the default speakers
- Added Trello and Github links in settings
- Enhanced Sound Library
### Fixed
- Fix microphone not inputting audio when in audio playback
- Fix CaptureComboBox not saving last device when changing

---

## [0.1.4] - 2025-5-08
### Added
- Added Loop Checkbox
- Play/Pause/Stop UI and sound Optimization
### Fixed
- Fixed hen an audio is paused, if another one is played the old plays instead.

## [0.1.5] - 2025-5-17
### Added
- First version of soundboard management
### Fixed
- Fixed app data bug
- Partially fixed driver checks and drivers not installing on some devices

## [0.1.6] - 2025-5-31
### Added
- Added Audio Controls like Bass and Pitch
- Added TTS integrated with ESpeak
- Added New UI for Sounds tab
### Fixed
- Fixed pitch and bass bugs
- Fixed app not finding espeak data files
- Fixed speed formula for TTS
- Fixed UI Issues
- Allowed Minimize and Maximize


## [0.1.7] - 2025-7-??
### Added
- ?
### Fixed
- Persistant audio bug fixed
