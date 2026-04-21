using Dalamud.Interface.Utility.Raii;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Bindings.ImGui;
using Lumina.Excel.Sheets;
using visland.Helpers;

namespace visland.Farm;

public unsafe class FarmWindow : UIAttachedWindow
{
    private FarmConfig _config;
    private FarmDebug _debug = new();

    public FarmWindow() : base(Loc.Tr("Farm Automation", "农场自动化"), "MJIFarmManagement", new(400, 600))
    {
        _config = Service.Config.Get<FarmConfig>();
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
        var agent = AgentMJIFarmManagement.Instance();
        if (mji == null || mji->FarmState == null || mji->IslandState.Farm.EligibleForCare == 0 || agent == null)
        {
            ImGui.TextUnformatted(Loc.Tr("Mammets not available!", "暂无可用的工匠人偶！"));
            return;
        }

        DrawGlobalOperations();
        ImGui.Separator();
        DrawPlotOperations();
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
            bool canDismiss = false, canEntrust = false;
            var agent = AgentMJIFarmManagement.Instance();
            for (var i = 0; i < agent->NumSlots; ++i)
            {
                var cared = agent->Slots[i].UnderCare;
                canDismiss |= cared;
                canEntrust |= !cared && agent->Slots[i].SeedItemId != 0;
            }

            using (ImRaii.Disabled(!canDismiss))
                if (ImGui.Button(Loc.Tr("Dismiss all", "全部解雇")))
                    DismissAll();
            ImGui.SameLine();
            using (ImRaii.Disabled(!canEntrust))
                if (ImGui.Button(Loc.Tr("Entrust all", "全部委托")))
                    EntrustAll();
        }
    }

    private void DrawPlotOperations()
    {
        using var table = ImRaii.Table("table", 2);
        if (table)
        {
            ImGui.TableSetupColumn(Loc.Tr("Slot", "地块"));
            ImGui.TableSetupColumn(Loc.Tr("Operations", "操作"));
            ImGui.TableHeadersRow();

            var agent = AgentMJIFarmManagement.Instance();
            for (var i = 0; i < agent->NumSlots; ++i)
            {
                ref var slot = ref agent->Slots[i];
                var inventory = Utils.NumItems(slot.YieldItemId);
                var overcap = inventory + slot.YieldAvailable > 999;
                var full = inventory == 999;

                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                using (ImRaii.PushColor(ImGuiCol.Text, full ? 0xff0000ff : 0xff00ffff, overcap))
                    ImGuiEx.TextV($"{slot.YieldName}: {inventory} + {slot.YieldAvailable} / 999");

                ImGui.TableNextColumn();
                if (slot.YieldAvailable > 0)
                {
                    using (ImRaii.Disabled(full))
                    {
                        if (ImGui.Button($"{Loc.Tr("Collect", "收取")}##{i}"))
                            CollectOne(i, false);
                        ImGui.SameLine();
                        if (ImGui.Button($"{Loc.Tr("Collect & dismiss", "收取并解雇")}##{i}"))
                            CollectOne(i, true);
                    }
                }
                else if (slot.UnderCare)
                {
                    if (ImGui.Button($"{Loc.Tr("Dismiss", "解雇")}##{i}"))
                        DismissOne(i);
                }
                else if (slot.SeedItemId != 0)
                {
                    if (slot.WasUnderCare || Utils.NumCowries() >= 5)
                    {
                        if (ImGui.Button($"{Loc.Tr("Entrust", "委托")}##{i}"))
                            EntrustOne(i, slot.SeedItemId);
                    }
                    // else: not enough cowries
                }
                // TODO: else - choose what to plant?
            }
        }
    }

    private CollectResult CalculateCollectResult()
    {
        var agent = AgentMJIFarmManagement.Instance();
        var mji = MJIManager.Instance();
        if (agent == null || agent->TotalAvailableYield <= 0 || mji == null || mji->FarmState == null)
            return CollectResult.NothingToCollect;

        var sheet = Service.LuminaGameData.GetExcelSheet<MJICropSeed>()!;
        var perCropYield = new int[sheet.Count];
        for (var i = 0; i < 20; ++i)
        {
            var seed = mji->FarmState->SeedType[i];
            if (seed != 0)
            {
                perCropYield[seed] += mji->FarmState->GardenerYield[i];
            }
        }

        var anyOvercap = false;
        var allFull = true;
        for (var i = 1; i < perCropYield.Length; ++i)
        {
            if (perCropYield[i] == 0)
                continue;

            var inventory = Utils.NumItems(sheet.GetRow((uint)i)!.Item.RowId);
            allFull &= inventory >= 999;
            anyOvercap |= inventory + perCropYield[i] > 999;
        }
        return allFull ? CollectResult.EverythingCapped : anyOvercap ? CollectResult.CanCollectWithOvercap : CollectResult.CanCollectSafely;
    }

    private void CollectOne(int slot, bool dismissAfter)
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            Service.Log.Info($"Collecting slot {slot}, dismiss={dismissAfter}");
            if (dismissAfter)
                mji->FarmState->CollectSingleAndDismiss((uint)slot);
            else
                mji->FarmState->CollectSingle((uint)slot);
        }
    }

    private void CollectAll()
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            Service.Log.Info("Collecting everything from farm");
            mji->FarmState->UpdateExpectedTotalYield();
            mji->FarmState->CollectAll(true);
        }
    }

    private void DismissOne(int slot)
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            Service.Log.Info($"Dismissing slot {slot}");
            mji->FarmState->Dismiss((uint)slot);
        }
    }

    private void DismissAll()
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            Service.Log.Info($"Dismissing all");
            for (var i = 0; i < 20; ++i)
            {
                if (mji->FarmState->FarmSlotFlags[i].HasFlag(FarmSlotFlags.UnderCare))
                    mji->FarmState->Dismiss((uint)i);
            }
        }
    }

    private void EntrustOne(int slot, uint seedId)
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            Service.Log.Info($"Entrusting slot {slot}, planting {seedId}");
            mji->FarmState->Entrust((uint)slot, seedId);
        }
    }

    private void EntrustAll()
    {
        var mji = MJIManager.Instance();
        if (mji != null && mji->FarmState != null)
        {
            Service.Log.Info($"Entrusting all");
            for (var i = 0; i < 20; ++i)
            {
                var seed = mji->FarmState->SeedType[i];
                if (seed != 0 && !mji->FarmState->FarmSlotFlags[i].HasFlag(FarmSlotFlags.UnderCare))
                {
                    mji->FarmState->Entrust((uint)i, mji->FarmState->SeedItemIds.AsSpan()[seed]);
                }
            }
        }
    }
}
