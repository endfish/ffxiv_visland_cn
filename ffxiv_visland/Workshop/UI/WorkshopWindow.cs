using Dalamud.Interface.Utility.Raii;
using visland.Helpers;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace visland.Workshop;

unsafe class WorkshopWindow : UIAttachedWindow {
    private readonly WorkshopConfig _config;
    private readonly WorkshopManual _manual = new();
    private readonly WorkshopOCImport _oc = new();
    private readonly WorkshopDebug _debug = new();

    public WorkshopWindow() : base(Loc.Tr("Workshop automation", "工坊自动化"), "MJICraftSchedule", new(500, 650)) {
        _config = Service.Config.Get<WorkshopConfig>();
    }

    public override void PreOpenCheck() {
        base.PreOpenCheck();
        var agent = AgentMJICraftSchedule.Instance();
        IsOpen &= agent != null && agent->Data != null;

        _oc.Update();
    }

    public override void Draw() {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs) {
            using (var tab = ImRaii.TabItem(Loc.Tr("Schedule", "排班")))
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

    public override void OnOpen() {
        if (_config.AutoOpenNextDay) {
            WorkshopUtils.SetCurrentCycle(AgentMJICraftSchedule.Instance()->Data->CycleInProgress + 1);
        }
        if (_config.FavourMode == FavourMode.MinMaxFreeRestDay)
            WorkshopUtils.VoidSecondRestThisWeek();
        if (_config.AutoImport)
            _oc.LoadSeasonRecs(false, silent: true);
    }

    private void DrawSettings() {
        if (ImGui.Checkbox(Loc.Tr("Automatically select next cycle on open", "打开时自动切到下一周期"), ref _config.AutoOpenNextDay))
            _config.NotifyModified();
        if (ImGui.Checkbox(Loc.Tr("Automatically load archive recs on open", "打开时自动加载历史推荐排班"), ref _config.AutoImport))
            _config.NotifyModified();

        ImGui.Separator();
        ImGui.TextUnformatted(Loc.Tr("Favour integration", "特供整合"));
        var mode = (int)_config.FavourMode;
        var modes = new[] {
            Loc.Tr("None — OC schedule only", "不整合——仅使用 OC 排班"),
            Loc.Tr("Replace workshop 4 — credit favours already in WS1-3", "替换第 4 工坊——先计入 1–3 工坊已经生产的特供"),
            Loc.Tr("Min-max — substitutions + sacrifice low-value slots", "收益优化——优先替换，必要时牺牲低收益时段"),
            Loc.Tr("Min-max + free rest day — craft on OC's second rest day", "收益优化 + 空闲休息日——在 OC 的第二个休息日生产"),
        };
        if (ImGui.Combo("##favourMode", ref mode, modes, modes.Length)) {
            _config.FavourMode = (FavourMode)mode;
            _config.NotifyModified();
        }
        ImGui.TextWrapped(_config.FavourMode switch {
            FavourMode.None => Loc.Tr(
                "Loads the archived Overseas Casuals schedule as-is. Use manual favour overrides if needed.",
                "原样加载 Overseas Casuals 历史排班；如有需要，可再手动覆盖特供排班。"),
            FavourMode.ReplaceWorkshop4 => Loc.Tr(
                "Workshops 1-3 keep the archive schedule. Workshop 4 is filled from the built-in favour solver, after crediting any favour crafts already produced by the recommended agenda.",
                "工坊 1–3 保留历史排班；先计入推荐排班已经生产的特供，再用内置求解器填充第 4 工坊。"),
            FavourMode.MinMax => Loc.Tr(
                "Tries same-duration/category substitutions first, then places remaining favours on the lowest-value workshop slots so high-cowrie days stay intact when possible.",
                "优先尝试时长与类别相容的替换，再把剩余特供放入收益最低的工坊时段，尽量保留高贝壳币收益日。"),
            FavourMode.MinMaxFreeRestDay => Loc.Tr(
                "Same as min-max, but turns the archive's second rest day into a crafting day (C1 stays rest) so most favours can land on a \"free\" day.",
                "与收益优化相同，但会把历史排班的第二个休息日改为生产日（C1 仍休息），尽量把特供安排到这个“空闲”日期。"),
            _ => "",
        });

        ImGui.Separator();
        if (ImGui.Checkbox(Loc.Tr("Show advanced favour override controls", "显示高级特供覆盖控件"), ref _config.UseFavourSolver))
            _config.NotifyModified();
        ImGui.TextWrapped(Loc.Tr(
            "Shows manual favour-solver / clipboard override buttons on the Schedule tab.",
            "在“排班”页显示手动特供求解及剪贴板覆盖按钮。"));
    }
}
