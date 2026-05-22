# AGENTS.md

## Scope

These instructions apply to the entire repository.

XelsTweaks is a modular Dalamud tweak hub. Keep the plugin shell small and put feature behavior in individual tweak classes.

## Agent Instruction Standards

- Treat this file as the durable repository instruction source for coding agents.
- Keep instructions concrete and verifiable. Prefer exact commands, paths, ownership boundaries, and review expectations over broad preferences.
- Keep user-facing installation and usage details in `README.md`; keep build, release, automation, and agent workflow rules in `AGENTS.md`.
- Do not add task-specific notes, transient plans, or duplicate large instruction blocks here.
- Do not use alternate instruction filenames unless Codex has been explicitly configured to discover them.

## Build And Validation

Use these commands from the repository root:

```bash
dotnet restore XelsTweaks/XelsTweaks.csproj
dotnet build XelsTweaks/XelsTweaks.csproj -c Release -p:EnableWindowsTargeting=true
dotnet format XelsTweaks/XelsTweaks.csproj --verify-no-changes
```

On Linux, set `DALAMUD_HOME` to a directory containing Dalamud dev assemblies. The project falls back to `$HOME/.xlcore/dalamud/Hooks/dev` when `DALAMUD_HOME` is not set.

## XelsDevBridge Runtime Inspection

Use the `xels-tweaks-devbridge` Codex skill, or the locally configured equivalent, when live runtime state would reduce guessing for tweak work. Good candidates include addon node inspection, AtkValue layouts, target/object state, condition flags, glamour dresser behavior, UI callback experiments, and in-game validation.

- Treat DevBridge as optional, evolving, and machine-specific. Do not hardcode local checkout paths, helper script paths, bridge URLs, token locations, or connection file paths in this repository's documentation.
- Start each investigation with the skill's discovery workflow and prefer advertised capabilities, routes, schemas, and action metadata over endpoint assumptions.
- Use read-only queries first. Mutation actions such as command execution, targeting, button clicks, or callback firing require a specific low-risk purpose, the smallest useful payload, and a final-response note describing the action used.
- If DevBridge is unavailable, continue from static code when reasonable and state the limitation. If runtime state is essential, propose the smallest missing bridge capability.
- After editing or rebuilding a local DevBridge instance, reload the running game plugin before trusting live node traversal or action metadata.

## Project Rules

- Direct commits to `main` are allowed for solo/agent work when appropriate; use pull requests when review or staging helps.
- Do not manually edit versions unless explicitly instructed.
- Do not use timestamp versions or CI build numbers as stable public versions.
- Do not change release workflows without explaining the effect.
- Do not publish to the official Dalamud repo.
- Do not commit secrets.
- Keep user-facing changelog text understandable for non-developers.
- Manual testing builds may only update testing/pre-release fields.
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

`.github/workflows/validate.yml`, `.github/workflows/publish-testing.yml`, and `.github/workflows/release.yml` are thin wrappers around reusable automation in `XelsPlugins/XelsDalamudRepo`.

Testing builds are manually triggered, use the mutable `testing` prerelease, and may only update central feed testing fields:

- `TestingAssemblyVersion`
- `TestingDalamudApiLevel`
- `DownloadLinkTesting`

Stable releases are manually triggered, use immutable `vX.Y.Z` tags, and may update central feed stable fields:

- `AssemblyVersion`
- `DownloadLinkInstall`
- `DownloadLinkUpdate`

Generated release notes belong on GitHub release and prerelease pages, not in `pluginmaster.json` changelog fields.

## Commit Message Standards

Use Conventional Commits for all agent-authored commits. The shared validation workflow checks non-merge commit subjects on pushes to `main`, and release automation validates the commit range before publishing.

- Use `fix:` or `perf:` for patch-level user-facing changes.
- Use `feat:` for minor user-facing additions.
- Use `type!:` or a `BREAKING CHANGE:` footer for major changes.
- Use `docs:`, `style:`, `refactor:`, `test:`, `build:`, `ci:`, or `chore:` for changes that should not create a user-facing stable bump unless breaking.
- Do not prefix commit subjects with `[codex]`.
- Keep the subject concise, imperative, and clear about the user impact.

Examples:

- `fix: keep command reference window open`
- `feat: add armoire automation tweak`
- `ci: migrate testing publish workflow`
- `docs: clarify testing build installation`
- `chore: update plugin metadata`
