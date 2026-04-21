using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using visland.Helpers;
using AtkValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace visland.Export;

unsafe class ExportWindow : UIAttachedWindow
{
    private ExportConfig _config;
    private ExportDebug _debug = new();
    private Throttle _exportThrottle = new(); // export seems to close & reopen window?..

    public ExportWindow() : base(Loc.Tr("Exports Automation", "出口自动化"), "MJIDisposeShop", new(400, 600))
    {
        _config = Service.Config.Get<ExportConfig>();
    }

    public override void PreOpenCheck()
    {
        base.PreOpenCheck();
        var agent = AgentMJIDisposeShop.Instance();
        IsOpen &= agent != null && agent->Data != null && agent->Data->DataInitialized;
    }

    public override void OnOpen()
    {
        if (_config.AutoSell)
        {
            _exportThrottle.Exec(AutoExport, 2);
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
        if (ImGui.Checkbox(Loc.Tr("Auto Export", "自动出货"), ref _config.AutoSell))
            _config.NotifyModified();
        ImGui.PushItemWidth(150);
        if (ImGui.SliderInt(Loc.Tr("Sell normal above", "普通材料保留上限"), ref _config.NormalLimit, 0, 999))
            _config.NotifyModified();
        if (ImGui.SliderInt(Loc.Tr("Sell granary above", "谷仓材料保留上限"), ref _config.GranaryLimit, 0, 999))
            _config.NotifyModified();
        if (ImGui.SliderInt(Loc.Tr("Sell farm above", "农场材料保留上限"), ref _config.FarmLimit, 0, 999))
            _config.NotifyModified();
        if (ImGui.SliderInt(Loc.Tr("Sell pasture above", "牧场材料保留上限"), ref _config.PastureLimit, 0, 999))
            _config.NotifyModified();
        ImGui.PopItemWidth();

        if (ImGui.Button(Loc.Tr("Sell everything above configured limits", "出售所有超过上限的物品")))
            AutoExport();
    }

    private void AutoExport()
    {
        try
        {
            var data = AgentMJIDisposeShop.Instance()->Data;
            int seafarerCowries = data->CurrencyCounts[0], islanderCowries = data->CurrencyCounts[1];
            AutoExportCategory(0, _config.NormalLimit, ref seafarerCowries, ref islanderCowries);
            AutoExportCategory(1, _config.GranaryLimit, ref seafarerCowries, ref islanderCowries);
            AutoExportCategory(2, _config.FarmLimit, ref seafarerCowries, ref islanderCowries);
            AutoExportCategory(3, _config.PastureLimit, ref seafarerCowries, ref islanderCowries);
        }
        catch (Exception ex)
        {
            Service.Log.Error($"Error: {ex}");
            Service.ChatGui.PrintError(Loc.Format("Auto export error: {0}", "自动出货出错：{0}", ex.Message));
        }
    }

    private void AutoExportCategory(int category, int limit, ref int seafarerCowries, ref int islanderCowries)
    {
        if (limit >= 999)
            return;
        var agent = AgentMJIDisposeShop.Instance();
        var data = agent->Data;
        List<AtkValue> args =
        [
            new() { Type = AtkValueType.UInt },
            new() { Type = AtkValueType.UInt, Int = limit }
        ];
        var numItems = 0;
        foreach (var item in data->PerCategoryItems[category].AsSpan())
        {
            var count = Utils.NumItems(item.Value->ItemId);
            if (count <= limit)
                continue;

            var export = count - limit;
            var value = item.Value->CowriesPerItem * export;
            if (item.Value->UseIslanderCowries)
            {
                islanderCowries += value;
                if (islanderCowries > data->CurrencyStackSizes[1])
                    throw new Exception(Loc.Tr("Islander cowries would overcap", "岛民贝壳币将会溢出"));
            }
            else
            {
                seafarerCowries += value;
                if (seafarerCowries > data->CurrencyStackSizes[0])
                    throw new Exception(Loc.Tr("Seafarer cowries would overcap", "海员贝壳币将会溢出"));
            }

            args.Add(new() { Type = AtkValueType.UInt, UInt = item.Value->ShopItemRowId });
            args.Add(new() { Type = AtkValueType.UInt, Int = export });
            if (++numItems > 64)
                throw new Exception(Loc.Tr("Too many items to export, please report this as a bug!", "要出货的物品太多了，请把这当成 bug 反馈。"));
        }
        var argsSpan = CollectionsMarshal.AsSpan(args);
        argsSpan[0].Int = numItems;

        Service.Log.Info(Loc.Format("Exporting {0} items above {1} limit...", "正在出货 {0} 个超过 {1} 上限的物品...", numItems, limit));
        var listener = *(AgentInterface**)((nint)agent + 0x18);
        Utils.SynthesizeEvent(listener, 0, argsSpan);
    }
}
