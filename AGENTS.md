# AGENTS.md

## Scope

These instructions apply to the entire repository.

XelsTweaks is a modular Dalamud tweak hub. Keep the plugin shell small and put feature behavior in individual tweak classes.

## Build And Validation

Use these commands from the repository root:

```bash
dotnet restore XelsTweaks/XelsTweaks.csproj
dotnet build XelsTweaks/XelsTweaks.csproj -c Release -p:EnableWindowsTargeting=true
dotnet format XelsTweaks/XelsTweaks.csproj --verify-no-changes
```

On Linux, set `DALAMUD_HOME` to a directory containing Dalamud dev assemblies. The project falls back to `$HOME/.xlcore/dalamud/Hooks/dev` when `DALAMUD_HOME` is not set.

## Project Rules

- No direct commits to `main` after branch protection is enabled.
- All work goes through feature branches and PRs.
- Commit messages and PR titles must use Conventional Commits.
- PR bodies must include release notes written for plugin users.
- Do not manually edit versions unless explicitly instructed.
- Do not use timestamp versions or CI build numbers as stable public versions.
- Do not change release workflows without explaining the effect.
- Do not publish to the official Dalamud repo.
- Do not commit secrets.
- Keep user-facing changelog text understandable for non-developers.
- PR builds may only update testing/pre-release fields.
- Manual stable releases may update stable fields.
- Keep `Plugin.cs` as the composition root: service injection, command registration, config/window wiring, and disposal only.
- Put tweak behavior under `XelsTweaks/Tweaks/`.
- Every tweak must own and dispose its own hooks, IPC subscribers, events, textures, and other resources.
- A failed tweak must not break the whole plugin. Route enable/disable through `TweakManager`.
- Do not add broad gameplay automation without an explicit purpose and risk note.
- Prefer direct Dalamud APIs over helper libraries until a real shared abstraction is justified.
- Use file-scoped namespaces, nullable reference types, explicit access modifiers, four-space indentation, and Allman braces.
- Use `IPluginLog` for logging. Do not use `Console.WriteLine` in plugin runtime code.
- Unregister slash commands, UI callbacks, framework events, and hooks in `Dispose()`.

## Tweak Guidelines

- Use stable IDs such as `interface.compactPartyList`.
- Keep names user-facing and descriptions short.
- Default new tweaks to disabled unless they are display-only and low risk.
- Keep tweak-specific config behind the owning tweak; use `TweakState.Settings` for simple scaffold settings or add typed config when a tweak needs it.
- Avoid update-loop work unless the tweak needs it. If it does, keep it lightweight and unsubscribe when disabled.

## Release And Metadata

The active custom feed is `XelsPlugins/XelsDalamudRepo`. Keep this repository listed in that repo's `repos.txt`.

`.github/workflows/validate.yml`, `.github/workflows/pr-preview.yml`, and `.github/workflows/release.yml` are thin wrappers around `XelsPlugins/XelsDalamud.Workflows`.

PR previews use mutable `pr-<PR_NUMBER>` prereleases and may only update central feed testing fields:

- `TestingAssemblyVersion`
- `TestingChangelog`
- `TestingDalamudApiLevel`
- `DownloadLinkTesting`

Stable releases are manually triggered, use immutable `vX.Y.Z` tags, and may update central feed stable fields:

- `AssemblyVersion`
- `DownloadLinkInstall`
- `DownloadLinkUpdate`
- stable changelog/release metadata
