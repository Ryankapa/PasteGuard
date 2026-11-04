# Changelog

All notable changes to PasteGuard. Versions follow [Semantic Versioning](https://semver.org):
**major** for breaking changes, **minor** for new features, **patch** for fixes.

The newest version's notes are shown in the app when someone checks for updates.

## [Unreleased]

## [1.2.1] - 2026-11-05

### Fixed
- **Updating from the app works now.** Before, it failed with "The new version couldn't be started", and **About** and **Open raw log** then crashed until you restarted PasteGuard. The cause: PasteGuard replaced its own file while it was still running. Now it closes first and the new version is put in place after.
- **On 1.1.0 or 1.2.0?** Download this version from GitHub once by hand, because the old updater is the broken part. After that, updates work from the app.

## [1.2.0] - 2026-11-05

### Changed
- PasteGuard now spots disguised ClickFix commands that used to slip past it: scrambled, encoded or split-up commands, hidden-window tricks and fake "I am not a robot" comments. It was tested against every command in the ClickFix Hunter feed (over 6,000 real attack pages) and sends all the Windows ones to Jev for a verdict.
- Look-alike characters, invisible characters, odd quotes and backslashed links (`http:/\`) can no longer hide a command.

## [1.1.0] - 2026-11-04

### Added
- **Check for updates…** in the tray menu and in Settings. PasteGuard asks GitHub for the latest release, shows what's new, then downloads it, checks its SHA-256 hash and restarts on the new version. Nothing is checked unless you click it.

## [1.0.0] - 2026-11-03

### Added
- First release: tray agent that watches the clipboard, scores command-like text with Jev and blocks ClickFix-style commands.
- Review window to keep a blocked command blocked or release it, with "Always allow this exact command".
- Threat activity window, Settings with a "Try it" box, and Start with Windows.
