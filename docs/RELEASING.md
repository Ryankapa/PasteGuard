# Releasing PasteGuard

The version lives in one place: `<Version>` in [Directory.Build.props](../Directory.Build.props).
The app, the exe's file version, the "About" menu, the update check and the GitHub release all read it from there.
Never edit it by hand, and never push a `v*` tag yourself. Use `release.ps1`.

Versions follow [Semantic Versioning](https://semver.org):

| Change | Bump | Example |
|---|---|---|
| Bug fixes only | `patch` | 1.1.0 → 1.1.1 |
| New features, nothing breaks | `minor` | 1.1.0 → 1.2.0 |
| Settings or behaviour change in a way that breaks existing setups | `major` | 1.1.0 → 2.0.0 |

## Steps

1. While working, add a line for each user-visible change under `## [Unreleased]` in [CHANGELOG.md](../CHANGELOG.md),
   grouped as `### Added`, `### Changed` or `### Fixed`. Write for users: these notes appear in the app's
   **Check for updates** window and on the release page.
2. Commit the work to `main`.
3. Run:
   ```powershell
   .\release.ps1 -Bump minor -Push
   ```
   It checks the tree is clean and on `main`, bumps the version, moves the Unreleased notes under the new version
   with today's date, runs the tests, commits `Release vX.Y.Z`, tags `vX.Y.Z` and pushes both. Leave out `-Push`
   to look at the commit and tag before pushing (`git push origin main vX.Y.Z`).
4. GitHub Actions (`release.yml`) refuses the tag unless it matches `Directory.Build.props` and the changelog has
   notes for it, then tests, builds `PasteGuard.exe` + `PasteGuard.exe.sha256`, and publishes the release with
   the changelog notes.
5. Within a few minutes, anyone who clicks **Check for updates…** is offered the new version.

## Don'ts

- Don't move or re-push an existing tag: people who already updated would have a different file than the
  release, and it rebuilds the exe with a new hash. Fix mistakes with a new patch version instead.
- Don't rename `PasteGuard.exe` or `PasteGuard.exe.sha256` in the release: the updater looks for those names.
- Don't publish a release as a pre-release or with a tag like `v2.0.0-beta`; the updater ignores those.
