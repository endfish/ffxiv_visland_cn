using Dalamud.Game;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Bindings.ImGui;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using visland.Helpers;

namespace visland.Workshop;

public unsafe class WorkshopOCImport {
    public WorkshopSolver.Recs Recommendations = new();

    private readonly WorkshopConfig _config;
    private readonly WorkshopSeasonDB _seasonDB;
    private readonly ExcelSheet<MJICraftworksObject> _craftSheet;
    private readonly List<string> _displayNames;
    private readonly List<string> _botNames;
    private readonly ClipboardParser _parser;
    private readonly ScheduleApplier _applier = new();
    private readonly FavourReader _favourReader;
    private readonly List<Func<bool>> _pendingActions = [];
    private int _loadedSeason;
    private bool _loadedNextWeek;

    public WorkshopOCImport() {
        _config = Service.Config.Get<WorkshopConfig>();
        _seasonDB = new WorkshopSeasonDB();
        _craftSheet = MJICraftworksObject.Get();
        _displayNames = [.. _craftSheet.Select(r => r.Item.Value.Name.ToString())];
        _botNames = [.. _craftSheet.Select(r => OSCHandler.OfficialNameToBotName(Item.GetRow(r.Item.RowId)!.Value.WithLanguage(ClientLanguage.English).Name.ExtractText()))];
        _parser = new(_craftSheet, _botNames);
        _favourReader = new(_botNames);
    }

    public void Update() {
        var numDone = _pendingActions.TakeWhile(f => f()).Count();
        _pendingActions.RemoveRange(0, numDone);
    }

    public void Draw() {
        using var globalDisable = ImRaii.Disabled(_pendingActions.Count > 0);

        var thisSeason = _seasonDB.CurrentSeason(false);
        var nextSeason = _seasonDB.CurrentSeason(true);
        ImGui.TextUnformatted(Loc.Format(
            "Archive seasons {0}-{1} (cycle {2})",
            "历史排班赛季 {0}–{1}（循环长度 {2}）",
            _seasonDB.RangeStart,
            _seasonDB.RangeEnd,
            _seasonDB.CycleLength));
        var thisSeasonSuffix = _seasonDB.TryGet(thisSeason, out var cur) ? $" ({cur.Date})" : Loc.Tr(" (missing)", "（缺失）");
        var nextSeasonSuffix = _seasonDB.TryGet(nextSeason, out var nxt) ? $" ({nxt.Date})" : Loc.Tr(" (missing)", "（缺失）");
        ImGui.TextUnformatted(Loc.Format("This week → Season {0}{1}", "本周 → 第 {0} 赛季{1}", thisSeason, thisSeasonSuffix));
        ImGui.TextUnformatted(Loc.Format("Next week → Season {0}{1}", "下周 → 第 {0} 赛季{1}", nextSeason, nextSeasonSuffix));

        if (ImGui.Button(Loc.Tr("Load This Week", "加载本周排班")))
            LoadSeasonRecs(false);
        ImGui.SameLine();
        if (ImGui.Button(Loc.Tr("Load Next Week", "加载下周排班")))
            LoadSeasonRecs(true);
        ImGuiComponents.HelpMarker(Loc.Tr(
            "Loads Overseas Casuals archive recommendations for the mapped season, then applies the favour mode from Settings.",
            "加载对应赛季的 Overseas Casuals 历史推荐，并应用“设置”页选择的特供整合模式。"));

        if (ImGui.Button(Loc.Tr("Import Recommendations From Clipboard", "从剪贴板导入推荐排班")))
            ImportRecsFromClipboard(false);
        ImGuiComponents.HelpMarker(Loc.Tr(
            "Legacy importer for schedules copied from Discord.\n" +
            "The importer detects item names (without \"Isleworks\" et al) on each line.\n" +
            "You can copy an entire workshop schedule from discord, junk included.",
            "用于导入从 Discord 复制的旧格式排班。\n" +
            "导入器会识别每一行中的物品名，不需要“Isleworks”等前缀。\n" +
            "可以直接复制整段工坊排班，其中夹杂的无关文字会被忽略。"));
        ImGui.TextWrapped(Loc.Tr(
            "Chinese servers are also supported: you can paste the Tencent Docs format like 'D1: Rest' or 'D2: 3x Pumpkin Pudding, ...'.",
            "国服也支持：可以直接粘贴腾讯文档格式，例如“D1:休息”或“D2:3×新薯沙拉、五海杂烩汤、无人面包、五海杂烩汤”。"));
        ImGui.TextWrapped(Loc.Tr(
            "If the sheet uses '3x ...' and you want to keep workshop 4 for favours, enable 'Ignore 4th Workshop' before applying.",
            "如果排班表写的是“3×……”，并且想把第 4 工坊留给特供，请在应用前勾选“忽略第 4 工坊”。"));

        if (Recommendations.Empty)
            return;

        if (_loadedSeason != 0)
            ImGui.TextUnformatted(Loc.Format(
                "Loaded season {0}{1}",
                "已加载第 {0} 赛季{1}",
                _loadedSeason,
                _loadedNextWeek ? Loc.Tr(" (next week)", "（下周）") : Loc.Tr(" (this week)", "（本周）")));

        ImGui.Separator();

        if (_config.UseFavourSolver) {
            ImGui.TextUnformatted(Loc.Tr("Advanced favour overrides", "高级特供覆盖"));
            ImGuiComponents.HelpMarker(Loc.Tr(
                "Manual overrides for the currently loaded schedule. Archive loads already apply the favour mode from Settings.",
                "手动覆盖当前加载的排班。加载历史排班时已经应用了“设置”页中的特供整合模式。"));

            ImGui.TextV(Loc.Tr("Override 4th workshop with favours:", "用特供覆盖第 4 工坊："));
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Tr("This Week", "本周")}##4th"))
                OverrideSideRecsLastWorkshopSolver(false);
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Tr("Next Week", "下周")}##4th"))
                OverrideSideRecsLastWorkshopSolver(true);

            ImGui.TextV(Loc.Tr("Override closest workshops with favours:", "用特供尽快覆盖可用工坊："));
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Tr("This Week", "本周")}##asap"))
                OverrideSideRecsAsapSolver(false);
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Tr("Next Week", "下周")}##asap"))
                OverrideSideRecsAsapSolver(true);

            if (ImGui.Button(Loc.Tr("Override 4th workshop from clipboard", "用剪贴板排班覆盖第 4 工坊")))
                OverrideSideRecsLastWorkshopClipboard();
            if (ImGui.Button(Loc.Tr("Override closest workshops from clipboard", "用剪贴板排班尽快覆盖可用工坊")))
                OverrideSideRecsAsapClipboard();

            if (ImGuiComponents.IconButtonWithText(Dalamud.Interface.FontAwesomeIcon.Clipboard, Loc.Tr("Copy /favors (this week)", "复制 /favors（本周）"))) {
                try {
                    ImGui.SetClipboardText(_favourReader.CreateFavourRequestCommand(false));
                }
                catch (Exception ex) {
                    ReportError(ex.Message);
                }
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(Dalamud.Interface.FontAwesomeIcon.Clipboard, Loc.Tr("Copy /favors (next week)", "复制 /favors（下周）"))) {
                try {
                    ImGui.SetClipboardText(_favourReader.CreateFavourRequestCommand(true));
                }
                catch (Exception ex) {
                    ReportError(ex.Message);
                }
            }

            ImGui.Separator();
        }

        ImGui.TextV(Loc.Tr("Set Schedule:", "应用排班："));
        ImGui.SameLine();
        if (ImGui.Button(Loc.Tr("This Week", "本周")))
            ApplyRecommendations(false);
        ImGui.SameLine();
        if (ImGui.Button(Loc.Tr("Next Week", "下周")))
            ApplyRecommendations(true);
        ImGui.SameLine();
        var ignoreFourth = _applier.IgnoreFourthWorkshop;
        if (ImGui.Checkbox(Loc.Tr("Ignore 4th Workshop", "忽略第 4 工坊"), ref ignoreFourth))
            _applier.IgnoreFourthWorkshop = ignoreFourth;
        ImGui.Separator();

        DrawCycleRecommendations();
    }

    public void ImportRecsFromClipboard(bool silent) {
        try {
            Recommendations = _parser.ParseRecs(ImGui.GetClipboardText());
            _loadedSeason = 0;
        }
        catch (Exception ex) {
            ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message), silent);
        }
    }

    public void LoadSeasonRecs(bool nextWeek, bool silent = false) {
        try {
            if (_config.FavourMode == FavourMode.None) {
                ApplySeason(nextWeek, null);
                return;
            }

            _favourReader.EnsureDemandFavoursAvailable(_pendingActions);
            _pendingActions.Add(() => {
                try {
                    ApplySeason(nextWeek, _favourReader.ReadFavourState(nextWeek));
                }
                catch (Exception ex) {
                    ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message), silent);
                }
                return true;
            });
        }
        catch (Exception ex) {
            ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message), silent);
        }
    }

    private void ApplySeason(bool nextWeek, WorkshopSolver.FavourState? favours) {
        var season = _seasonDB.CurrentSeason(nextWeek);
        var baseRecs = _seasonDB.BuildRecs(season);
        Recommendations = favours == null || _config.FavourMode == FavourMode.None
            ? baseRecs
            : FavourIntegration.Apply(baseRecs, _config.FavourMode, favours.Value, _craftSheet, _seasonDB.RestCycles(season));
        _loadedSeason = season;
        _loadedNextWeek = nextWeek;
        Service.Log.Info($"Loaded workshop season {season} (favour mode {_config.FavourMode})");
    }

    private void DrawCycleRecommendations() {
        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.NoKeepColumnsVisible;
        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();

        using var scrollSection = ImRaii.Child("ScrollableSection");
        foreach ((var c, var r) in Recommendations.Enumerate()) {
            ImGui.TextV(Loc.Format("Cycle {0}:", "周期 {0}：", c));
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Tr("Set on Active Cycle", "设置到当前周期")}##{c}"))
                _applier.ApplyRecommendationToCurrentCycle(r);

            using var outerTable = ImRaii.Table($"table_{c}", r.Workshops.Count, tableFlags);
            if (outerTable) {
                var workshopLimit = r.Workshops.Count - (_applier.IgnoreFourthWorkshop && r.Workshops.Count > 1 ? 1 : 0);
                if (r.Workshops.Count <= 1) {
                    ImGui.TableSetupColumn(_applier.IgnoreFourthWorkshop
                        ? Loc.Format("Workshops 1-{0}", "工坊 1–{0}", maxWorkshops - 1)
                        : Loc.Tr("All Workshops", "全部工坊"));
                }
                else if (r.Workshops.Count < maxWorkshops) {
                    var numDuplicates = 1 + maxWorkshops - r.Workshops.Count;
                    ImGui.TableSetupColumn(Loc.Format("Workshops 1-{0}", "工坊 1–{0}", numDuplicates));
                    for (var i = 1; i < workshopLimit; ++i)
                        ImGui.TableSetupColumn(Loc.Format("Workshop {0}", "工坊 {0}", i + numDuplicates));
                }
                else {
                    for (var i = 0; i < workshopLimit; ++i)
                        ImGui.TableSetupColumn(Loc.Format("Workshop {0}", "工坊 {0}", i + 1));
                }
                ImGui.TableHeadersRow();

                ImGui.TableNextRow();
                for (var i = 0; i < workshopLimit; ++i) {
                    ImGui.TableNextColumn();
                    using var innerTable = ImRaii.Table($"table_{c}_{i}", 2, tableFlags);
                    if (innerTable) {
                        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
                        foreach (var rec in r.Workshops[i].Slots) {
                            ImGui.TableNextRow();

                            ImGui.TableNextColumn();
                            var iconSize = ImGui.GetTextLineHeight() * 1.5f;
                            var iconSizeVec = new Vector2(iconSize, iconSize);
                            var craftworkItemIcon = _craftSheet.GetRow(rec.CraftObjectId)!.Item.Value!.Icon;
                            ImGui.Image(Service.TextureProvider.GetFromGameIcon(new GameIconLookup(craftworkItemIcon)).GetWrapOrEmpty().Handle, iconSizeVec, Vector2.Zero, Vector2.One);

                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(_displayNames[(int)rec.CraftObjectId]);
                        }
                    }
                }
            }
        }
    }

    private void OverrideSideRecsLastWorkshopClipboard() {
        try {
            var overrideRecs = _parser.ParseRecOverrides(ImGui.GetClipboardText());
            if (overrideRecs.Count > Recommendations.Schedules.Count)
                throw new Exception(Loc.Format(
                    "Override list is longer than base schedule: {0} > {1}",
                    "覆盖列表比基础排班更长：{0} > {1}",
                    overrideRecs.Count,
                    Recommendations.Schedules.Count));
            OverrideSideRecsLastWorkshop(overrideRecs);
        }
        catch (Exception ex) {
            ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message));
        }
    }

    private void OverrideSideRecsLastWorkshopSolver(bool nextWeek) {
        _favourReader.EnsureDemandFavoursAvailable(_pendingActions);
        _pendingActions.Add(() => {
            OverrideSideRecsLastWorkshop(_favourReader.SolveRecOverrides(nextWeek));
            return true;
        });
    }

    private void OverrideSideRecsLastWorkshop(List<WorkshopSolver.WorkshopRec> overrides) {
        foreach ((var r, var o) in Recommendations.Schedules.Zip(overrides)) {
            if (r.Workshops.Count > 1)
                r.Workshops.RemoveAt(r.Workshops.Count - 1);
            r.Workshops.Add(o);
        }
        if (overrides.Count > Recommendations.Schedules.Count)
            Service.ChatGui.Print(Loc.Tr("Warning: couldn't fit all overrides into base schedule", "警告：无法将所有覆盖排班完整塞入基础排班"), Plugin.Name);
    }

    private void OverrideSideRecsAsapClipboard() {
        try {
            var overrideRecs = _parser.ParseRecOverrides(ImGui.GetClipboardText());
            if (overrideRecs.Count > Recommendations.Schedules.Count * 4)
                throw new Exception(Loc.Format(
                    "Override list is longer than base schedule: {0} > 4 * {1}",
                    "覆盖列表比基础排班更长：{0} > 4 × {1}",
                    overrideRecs.Count,
                    Recommendations.Schedules.Count));
            OverrideSideRecsAsap(overrideRecs);
        }
        catch (Exception ex) {
            ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message));
        }
    }

    private void OverrideSideRecsAsapSolver(bool nextWeek) {
        _favourReader.EnsureDemandFavoursAvailable(_pendingActions);
        _pendingActions.Add(() => {
            OverrideSideRecsAsap(_favourReader.SolveRecOverrides(nextWeek));
            return true;
        });
    }

    private void OverrideSideRecsAsap(List<WorkshopSolver.WorkshopRec> overrides) {
        var nextOverride = 0;
        foreach (var r in Recommendations.Schedules) {
            var batchSize = Math.Min(4, overrides.Count - nextOverride);
            if (batchSize == 0)
                break;

            if (r.Workshops.Count > 1)
                r.Workshops.RemoveAt(r.Workshops.Count - 1);
            var maxLeft = 4 - batchSize;
            if (r.Workshops.Count > maxLeft)
                r.Workshops.RemoveRange(maxLeft, r.Workshops.Count - maxLeft);
            r.Workshops.AddRange(overrides.Skip(nextOverride).Take(batchSize));
            nextOverride += batchSize;
        }
        if (nextOverride < overrides.Count)
            Service.ChatGui.Print(Loc.Tr("Warning: couldn't fit all overrides into base schedule", "警告：无法将所有覆盖排班完整塞入基础排班"), Plugin.Name);
    }

    private void ApplyRecommendations(bool nextWeek) {
        try {
            _applier.ApplyRecommendations(Recommendations, nextWeek);
        }
        catch (Exception ex) {
            ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message));
        }
    }

    private static void ReportError(string msg, bool silent = false) {
        Service.Log.Error(msg);
        if (!silent)
            Service.ChatGui.PrintError(msg);
    }
}
