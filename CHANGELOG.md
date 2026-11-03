# Changelog

All notable changes to PasteGuard. Versions follow [Semantic Versioning](https://semver.org):
**major** for breaking changes, **minor** for new features, **patch** for fixes.

The newest version's notes are shown in the app when someone checks for updates.

## [Unreleased]

## [1.1.0] - 2026-11-04

### Added
- **Check for updates…** in the tray menu and in Settings. PasteGuard asks GitHub for the latest release, shows what's new, then downloads it, checks its SHA-256 hash and restarts on the new version. Nothing is checked unless you click it.

## [1.0.0] - 2026-11-03

### Added
- First release: tray agent that watches the clipboard, scores command-like text with Jev and blocks ClickFix-style commands.
- Review window to keep a blocked command blocked or release it, with "Always allow this exact command".
- Threat activity window, Settings with a "Try it" box, and Start with Windows.
