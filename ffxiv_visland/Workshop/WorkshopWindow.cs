using Dalamud.Interface.Utility.Raii;
using visland.Helpers;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace visland.Workshop;

unsafe class WorkshopWindow : UIAttachedWindow
{
    private WorkshopConfig _config;
    private WorkshopManual _manual = new();
    private WorkshopOCImport _oc = new();
    private WorkshopDebug _debug = new();

    public WorkshopWindow() : base(Loc.Tr("Workshop automation", "工坊自动化"), "MJICraftSchedule", new(500, 650))
    {
        _config = Service.Config.Get<WorkshopConfig>();
    }

    public override void PreOpenCheck()
    {
        base.PreOpenCheck();
        var agent = AgentMJICraftSchedule.Instance();
        IsOpen &= agent != null && agent->Data != null;

        _oc.Update();
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs)
        {
            using (var tab = ImRaii.TabItem(Loc.Tr("OC import", "OC 导入")))
                if (tab)
                    _oc.Draw();
            using (var tab = ImRaii.TabItem(Loc.Tr("Manual schedule", "手动排班")))
                if (tab)
                    _manual.Draw();
            using (var tab = ImRaii.TabItem(Loc.Tr("Settings", "设置")))
                if (tab)
                    DrawSettings();
            using (var tab = ImRaii.TabItem(Loc.Tr("Debug", "调试")))
                if (tab)
                    _debug.Draw();
        }
    }

    public override void OnOpen()
    {
        if (_config.AutoOpenNextDay)
        {
            WorkshopUtils.SetCurrentCycle(AgentMJICraftSchedule.Instance()->Data->CycleInProgress + 1);
        }
        if (_config.AutoImport)
        {
            _oc.ImportRecsFromClipboard(true);
        }
    }

    private void DrawSettings()
    {
        if (ImGui.Checkbox(Loc.Tr("Automatically select next cycle on open", "打开时自动切到下一周期"), ref _config.AutoOpenNextDay))
            _config.NotifyModified();
        if (ImGui.Checkbox(Loc.Tr("Automatically import base recs on open", "打开时自动导入基础推荐排班"), ref _config.AutoImport))
            _config.NotifyModified();
        if (ImGui.Checkbox(Loc.Tr("Use experimental favor solver", "使用实验性特供求解器"), ref _config.UseFavorSolver))
            _config.NotifyModified();
    }
}
