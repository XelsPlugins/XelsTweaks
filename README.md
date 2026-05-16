# XelsTweaks

XelsTweaks is a scaffold for a modular Dalamud tweak hub, similar in shape to plugins such as SimpleTweaks, HaselTweaks, and Pandora.

The first version contains the plugin shell, config window, tweak manager, and one harmless example tweak you can copy when adding real features.

## Build

```bash
dotnet restore XelsTweaks/XelsTweaks.csproj
dotnet build XelsTweaks/XelsTweaks.csproj -c Release -p:EnableWindowsTargeting=true
```

On Linux, make sure Dalamud dev assemblies are available at `$DALAMUD_HOME` or `$HOME/.xlcore/dalamud/Hooks/dev`.

## Installation

Add the following URL to Dalamud's custom plugin repositories:

```text
https://raw.githubusercontent.com/Xeltor/XelsDalamudRepo/main/pluginmaster.json
```

Stable builds are published manually. Testing builds are generated from PR previews and require Dalamud's plugin testing versions option.

## Commands

- `/xelstweaks` or `/xt` opens the config window.
- `/xelstweaks list` prints registered tweak IDs.
- `/xelstweaks on <id>` enables a tweak.
- `/xelstweaks off <id>` disables a tweak.
- `/xelstweaks toggle <id>` toggles a tweak.

The same subcommands work with `/xt`.

## Adding Tweaks

1. Add a class under `XelsTweaks/Tweaks/<Category>/`.
2. Inherit from `TweakBase`.
3. Override `OnEnable`, `OnDisable`, and optionally `DrawConfig`.
4. Register it in `TweakManager.RegisterTweaks`.
