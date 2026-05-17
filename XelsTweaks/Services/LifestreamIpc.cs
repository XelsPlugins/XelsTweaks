using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace XelsTweaks.Services;

internal sealed class LifestreamIpc
{
    private readonly ICallGateSubscriber<bool> canAutoLogin;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<string, string, bool> connectAndLogin;
    private readonly ICallGateSubscriber<bool> canInitiateTravelFromCharaSelectList;
    private readonly ICallGateSubscriber<string, string, bool> initiateLoginFromCharaSelectScreen;

    public LifestreamIpc(IDalamudPluginInterface pluginInterface)
    {
        this.canAutoLogin = pluginInterface.GetIpcSubscriber<bool>("Lifestream.CanAutoLogin");
        this.isBusy = pluginInterface.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        this.connectAndLogin = pluginInterface.GetIpcSubscriber<string, string, bool>("Lifestream.ConnectAndLogin");
        this.canInitiateTravelFromCharaSelectList = pluginInterface.GetIpcSubscriber<bool>("Lifestream.CanInitiateTravelFromCharaSelectList");
        this.initiateLoginFromCharaSelectScreen = pluginInterface.GetIpcSubscriber<string, string, bool>("Lifestream.InitiateLoginFromCharaSelectScreen");
    }

    public bool TryCanAutoLogin(out bool result, out string? error)
    {
        return TryInvoke(this.canAutoLogin, "Lifestream auto-login", out result, out error);
    }

    public bool TryIsBusy(out bool result, out string? error)
    {
        return TryInvoke(this.isBusy, "Lifestream status", out result, out error);
    }

    public bool TryCanInitiateTravelFromCharaSelectList(out bool result, out string? error)
    {
        return TryInvoke(
            this.canInitiateTravelFromCharaSelectList,
            "Lifestream character select login",
            out result,
            out error);
    }

    public bool TryConnectAndLogin(string characterName, string homeWorld, out bool result, out string? error)
    {
        return TryInvoke(
            this.connectAndLogin,
            "Lifestream login",
            characterName,
            homeWorld,
            out result,
            out error);
    }

    public bool TryInitiateLoginFromCharaSelectScreen(string characterName, string homeWorld, out bool result, out string? error)
    {
        return TryInvoke(
            this.initiateLoginFromCharaSelectScreen,
            "Lifestream character select login",
            characterName,
            homeWorld,
            out result,
            out error);
    }

    private static bool TryInvoke(ICallGateSubscriber<bool> subscriber, string name, out bool result, out string? error)
    {
        result = false;
        if (!subscriber.HasFunction)
        {
            error = $"{name} is unavailable.";
            return false;
        }

        try
        {
            result = subscriber.InvokeFunc();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{name} failed: {ex.Message}";
            return false;
        }
    }

    private static bool TryInvoke(
        ICallGateSubscriber<string, string, bool> subscriber,
        string name,
        string characterName,
        string homeWorld,
        out bool result,
        out string? error)
    {
        result = false;
        if (!subscriber.HasFunction)
        {
            error = $"{name} is unavailable.";
            return false;
        }

        try
        {
            result = subscriber.InvokeFunc(characterName, homeWorld);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{name} failed: {ex.Message}";
            return false;
        }
    }
}
