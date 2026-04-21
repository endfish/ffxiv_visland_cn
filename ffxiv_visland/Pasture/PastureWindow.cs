using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using Dalamud.Bindings.ImGui;
using visland.Helpers;
using ECommons.ImGuiMethods;

namespace visland.Pasture;

unsafe class PastureWindow : UIAttachedWindow
{
    private PastureConfig _config;
    private PastureDebug _debug = new();

    public PastureWindow() : base(Loc.Tr("Pasture Automation", "牧场自动化"), "MJIAnimalManagement", new(400, 600))
    {
        _config = Service.Config.Get<PastureConfig>();
    }

    public override void PreOpenCheck()
    {
        base.PreOpenCheck();
        var agent = AgentMJIAnimalManagement.Instance();
        IsOpen &= agent != null && !agent->UpdateNeeded;
    }

    public override void OnOpen()
    {
        if (_config.Collect != CollectStrategy.Manual)
        {
            var state = CalculateCollectResult();
            if (state == CollectResult.CanCollectSafely || _config.Collect == CollectStrategy.FullAuto && state == CollectResult.CanCollectWithOvercap)
            {
                CollectAll();
            }
        }
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs)
        {
            using (var tab = ImRaii.TabItem(Loc.Tr("Main", "主界面")))
                if (tab)
                    DrawMain();
            using (var tab = ImRaii.TabItem(Loc.Tr("Debug", "调试")))
                if (tab)
                    _debug.Draw();
        }
    }

    private void DrawMain()
    {
        if (UICombo.Enum(Loc.Tr("Auto Collect", "自动收取"), ref _config.Collect))
            _config.NotifyModified();
        ImGui.Separator();

        var mji = MJIManager.Instance();
        var agent = AgentMJIAnimalManagement.Instance();
        if (mji == null || mji->PastureHandler == null || mji->IslandState.Pasture.EligibleForCare == 0 || agent == null)
        {
            ImGui.TextUnformatted(Loc.Tr("Mammets not available!", "暂无可用的工匠人偶！"));
            return;
        }

        DrawGlobalOperations();
    }

    private void DrawGlobalOperations()
    {
        var res = CalculateCollectResult();
        if (res != CollectResult.NothingToCollect)
        {
            // if there's uncollected stuff - propose to collect everything
            using (ImRaii.Disabled(res == CollectResult.EverythingCapped))
            {
                if (ImGui.Button(Loc.Tr("Collect all", "全部收取")))
                    CollectAll();
                if (res != CollectResult.CanCollectSafely)
                {
                    ImGui.SameLine();
                    using (ImRaii.PushColor(ImGuiCol.Text, 0xff0000ff))
                        ImGuiEx.TextV(res == CollectResult.EverythingCapped
                            ? Loc.Tr("Inventory is full!", "背包已满！")
                            : Loc.Tr("Warning: some resources will overcap!", "警告：部分资源会溢出！"));
                }
            }
        }
        else
        {
            // TODO: think about any other global operations?
            ImGuiEx.TextV(Loc.Tr("Nothing to collect!", "没有可收取的产物！"));
        }
    }

    private CollectResult CalculateCollectResult()
    {
        var mji = MJIManager.Instance();
        if (mji == null || mji->PastureHandler == null)
            return CollectResult.NothingToCollect;

        var haveNone = true;
        var anyOvercap = false;
        var allFull = true;
        foreach (var (itemId, count) in mji->PastureHandler->AvailableMammetLeavings)
        {
            if (count <= 0)
                continue;
            haveNone = false;
            var inventory = Utils.NumItems(itemId);
            allFull &= inventory >= 999;
            anyOvercap |= inventory + count > 999;
        }

        return haveNone ? CollectResult.NothingToCollect : allFull ? CollectResult.EverythingCapped : anyOvercap ? CollectResult.CanCollectWithOvercap : CollectResult.CanCollectSafely;
    }

    private void CollectAll()
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->PastureHandler != null)
        {
            Service.Log.Info("Collecting everything from pasture");
            mji->PastureHandler->CollectLeavingsAll();
        }
    }
}
