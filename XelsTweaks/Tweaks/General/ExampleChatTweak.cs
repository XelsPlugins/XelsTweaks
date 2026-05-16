using System;
using Dalamud.Bindings.ImGui;

namespace XelsTweaks.Tweaks.General;

internal sealed class ExampleChatTweak : TweakBase
{
    public const string TweakId = "general.exampleChat";

    private const string PrintDisableMessageKey = "printDisableMessage";

    public ExampleChatTweak(DalamudServices services, TweakState state, Action saveConfig)
        : base(services, state, saveConfig)
    {
    }

    public override string Id => TweakId;
    public override string Name => "Example chat message";
    public override string Description => "Prints a chat line when enabled. Use this as a copyable starting point for real tweaks.";
    public override TweakCategory Category => TweakCategory.General;

    public override bool DrawConfig()
    {
        var changed = false;
        var printDisableMessage = this.GetBool(PrintDisableMessageKey, true);
        if (ImGui.Checkbox("Print a message when disabled", ref printDisableMessage))
        {
            this.SetBool(PrintDisableMessageKey, printDisableMessage);
            changed = true;
        }

        return changed;
    }

    protected override void OnEnable()
    {
        this.Services.ChatGui.Print("[XelsTweaks] Example chat tweak enabled.");
    }

    protected override void OnDisable()
    {
        if (this.GetBool(PrintDisableMessageKey, true))
        {
            this.Services.ChatGui.Print("[XelsTweaks] Example chat tweak disabled.");
        }
    }
}
