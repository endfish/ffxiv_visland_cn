using Dalamud.Game;
using Dalamud.Interface.Components;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using ECommons;
using ECommons.ImGuiMethods;
using FFXIVClientStructs.FFXIV.Client.Game.MJI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Dalamud.Bindings.ImGui;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using visland.Helpers;

namespace visland.Workshop;

public unsafe class WorkshopOCImport
{
    public WorkshopSolver.Recs Recommendations = new();

    private WorkshopConfig _config;
    private ExcelSheet<MJICraftworksObject> _craftSheet;
    private List<string> _displayNames;
    private List<string> _botNames;
    private List<List<string>> _searchAliases;
    private List<Func<bool>> _pendingActions = [];
    private bool IgnoreFourthWorkshop;

    public WorkshopOCImport()
    {
        _config = Service.Config.Get<WorkshopConfig>();
        _craftSheet = GenericHelpers.GetSheet<MJICraftworksObject>(); // unlocalised sheet can't be fetched in english
        _displayNames = _craftSheet.Select(r => r.Item.Value.Name.ExtractText()).ToList();
        _botNames = _craftSheet.Select(r => OfficialNameToBotName(GenericHelpers.GetRow<Item>(r.Item.RowId, ClientLanguage.English)!.Value.Name.ExtractText())).ToList();
        _searchAliases = _craftSheet.Select(BuildSearchAliases).ToList();
    }

    public void Update()
    {
        var numDone = _pendingActions.TakeWhile(f => f()).Count();
        _pendingActions.RemoveRange(0, numDone);
    }

    public void Draw()
    {
        using var globalDisable = ImRaii.Disabled(_pendingActions.Count > 0); // disallow any manipulations while delayed actions are in progress

        if (ImGui.Button(Loc.Tr("Import Recommendations From Clipboard", "从剪贴板导入推荐排班")))
            ImportRecsFromClipboard(false);
        ImGuiComponents.HelpMarker(Loc.Tr(
            "This is for importing schedules from the Overseas Casuals' Discord from your clipboard.\n" +
            "This importer detects the presence of an item's name (not including \"Isleworks\" et al) on each line.\n" +
            "You can copy an entire workshop's schedule from the discord, junk included.",
            "用于从剪贴板导入 Overseas Casuals Discord 里的工坊排班。\n" +
            "导入器会在每一行中识别物品名称，不包含 \"Isleworks\" 等前缀。\n" +
            "你可以直接复制 Discord 里的整段排班内容，夹杂的无关文字也没关系。"));
        ImGui.TextWrapped(Loc.Tr(
            "Chinese servers are also supported: you can paste the Tencent Docs format like 'D1: Rest' or 'D2: 3x Pumpkin Pudding, ...'.",
            "国服也支持：可以直接粘贴腾讯文档里的格式，例如“D1:休息”或“D2:3×新薯沙拉、五海杂烩汤、无人面包、五海杂烩汤”。"));
        ImGui.TextWrapped(Loc.Tr(
            "If the sheet uses '3x ...' and you want to keep workshop 4 for favors, enable 'Ignore 4th Workshop' before applying.",
            "如果排班表写的是“3×...”，并且你想把第 4 工坊留给特供，请在应用前勾选“忽略第 4 工坊”。"));

        if (Recommendations.Empty)
            return;

        ImGui.Separator();

        if (!_config.UseFavorSolver)
        {
            ImGui.TextUnformatted(Loc.Tr("Favours", "特供"));
            ImGuiComponents.HelpMarker(Loc.Tr(
                "Click the \"This Week's Favors\" or \"Next Week's Favors\" button to generate a bot command for the OC discord for your favors.\n" +
                "Then click the #bot-spam button to open discord to the channel, paste in the command and copy its output.\n" +
                "Finally, click the \"Override 4th workshop\" button to replace the regular recommendations with favor recommendations.",
                "点击“本周特供”或“下周特供”来生成发给 OC Discord 机器人的特供命令。\n" +
                "然后点击 #bot-spam 按钮打开对应频道，贴入命令并复制机器人的输出结果。\n" +
                "最后点击“用特供排班覆盖第 4 工坊”按钮，用特供排班替换常规推荐。"));

            if (ImGuiComponents.IconButtonWithText(Dalamud.Interface.FontAwesomeIcon.Clipboard, Loc.Tr("This Week's Favors", "本周特供")))
                ImGui.SetClipboardText(CreateFavorRequestCommand(false));
            ImGui.SameLine();
            if (ImGuiComponents.IconButtonWithText(Dalamud.Interface.FontAwesomeIcon.Clipboard, Loc.Tr("Next Week's Favors", "下周特供")))
                ImGui.SetClipboardText(CreateFavorRequestCommand(true));

            if (ImGui.Button(Loc.Tr("Overseas Casuals > #bot-spam", "Overseas Casuals > #bot-spam")))
                Util.OpenLink("discord://discord.com/channels/1034534280757522442/1034985297391407126");
            if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
                Util.OpenLink("https://discord.com/channels/1034534280757522442/1034985297391407126");
            ImGuiComponents.HelpMarker(Loc.Tr("\uE051: Discord app\n\uE052: Discord in browser", "\uE051: Discord 客户端\n\uE052: 浏览器中的 Discord"));

            if (ImGui.Button(Loc.Tr("Override 4th workshop with favor schedules from clipboard", "用剪贴板中的特供排班覆盖第 4 工坊")))
                OverrideSideRecsLastWorkshopClipboard();
            if (ImGui.Button(Loc.Tr("Override closest workshops with favor schedules from clipboard", "用剪贴板中的特供排班尽快覆盖可用工坊")))
                OverrideSideRecsAsapClipboard();
        }
        else
        {
            ImGuiEx.TextV(Loc.Tr("Override 4th workshop with favors:", "用特供覆盖第 4 工坊："));
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Tr("This Week", "本周")}##4th"))
                OverrideSideRecsLastWorkshopSolver(false);
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Tr("Next Week", "下周")}##4th"))
                OverrideSideRecsLastWorkshopSolver(true);

            ImGuiEx.TextV(Loc.Tr("Override closest workshops with favors:", "用特供尽快覆盖可用工坊："));
            ImGui.SameLine();

            if (ImGui.Button($"{Loc.Tr("This Week", "本周")}##asap"))
                OverrideSideRecsAsapSolver(false);
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Tr("Next Week", "下周")}##asap"))
                OverrideSideRecsAsapSolver(true);
        }

        ImGui.Separator();

        ImGuiEx.TextV(Loc.Tr("Set Schedule:", "应用排班："));
        ImGui.SameLine();
        if (ImGui.Button(Loc.Tr("This Week", "本周")))
            ApplyRecommendations(false);
        ImGui.SameLine();
        if (ImGui.Button(Loc.Tr("Next Week", "下周")))
            ApplyRecommendations(true);
        ImGui.SameLine();
        ImGui.Checkbox(Loc.Tr("Ignore 4th Workshop", "忽略第 4 工坊"), ref IgnoreFourthWorkshop);
        ImGui.Separator();

        DrawCycleRecommendations();
    }

    public void ImportRecsFromClipboard(bool silent)
    {
        try
        {
            Recommendations = ParseRecs(ImGui.GetClipboardText());
        }
        catch (Exception ex)
        {
            ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message), silent);
        }
    }

    private void DrawCycleRecommendations()
    {
        var tableFlags = ImGuiTableFlags.RowBg | ImGuiTableFlags.NoKeepColumnsVisible;
        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();

        using var scrollSection = ImRaii.Child("ScrollableSection");
        foreach (var (c, r) in Recommendations.Enumerate())
        {
            ImGuiEx.TextV(Loc.Format("Cycle {0}:", "周期 {0}：", c));
            ImGui.SameLine();
            if (ImGui.Button($"{Loc.Tr("Set on Active Cycle", "设置到当前周期")}##{c}"))
                ApplyRecommendationToCurrentCycle(r);

            using var outerTable = ImRaii.Table($"table_{c}", r.Workshops.Count, tableFlags);
            if (outerTable)
            {
                var workshopLimit = r.Workshops.Count - (IgnoreFourthWorkshop && r.Workshops.Count > 1 ? 1 : 0);
                if (r.Workshops.Count <= 1)
                {
                    ImGui.TableSetupColumn(IgnoreFourthWorkshop ? Loc.Format("Workshops 1-{0}", "工坊 1-{0}", maxWorkshops - 1) : Loc.Tr("All Workshops", "全部工坊"));
                }
                else if (r.Workshops.Count < maxWorkshops)
                {
                    var numDuplicates = 1 + maxWorkshops - r.Workshops.Count;
                    ImGui.TableSetupColumn(Loc.Format("Workshops 1-{0}", "工坊 1-{0}", numDuplicates));
                    for (var i = 1; i < workshopLimit; ++i)
                        ImGui.TableSetupColumn(Loc.Format("Workshop {0}", "工坊 {0}", i + numDuplicates));
                }
                else
                {
                    // favors
                    for (var i = 0; i < workshopLimit; ++i)
                        ImGui.TableSetupColumn(Loc.Format("Workshop {0}", "工坊 {0}", i + 1));
                }
                ImGui.TableHeadersRow();

                ImGui.TableNextRow();
                for (var i = 0; i < workshopLimit; ++i)
                {
                    ImGui.TableNextColumn();
                    using var innerTable = ImRaii.Table($"table_{c}_{i}", 2, tableFlags);
                    if (innerTable)
                    {
                        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed);
                        foreach (var rec in r.Workshops[i].Slots)
                        {
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

    private unsafe string CreateFavorRequestCommand(bool nextWeek)
    {
        var state = MJIManager.Instance()->FavorState;
        if (state == null || state->UpdateState != 2)
        {
            ReportError(Loc.Format("Favor data not available: {0}", "特供数据不可用：{0}", state->UpdateState));
            return "";
        }

        var sheetCraft = Service.LuminaGameData.GetExcelSheet<MJICraftworksObject>(Language.English)!;
        var res = "/favors";
        var offset = nextWeek ? 6 : 3;
        for (var i = 0; i < 3; ++i)
        {
            var id = state->CraftObjectIds[offset + i];
            // the bot doesn't like names with apostrophes because it "breaks their formulas"
            var name = sheetCraft.GetRow(id).Item.Value.Name;
            if (!name.IsEmpty)
                res += $" favor{i + 1}:{_botNames[id].Replace("\'", "")}";
        }
        return res;
    }

    private void OverrideSideRecsLastWorkshopClipboard()
    {
        try
        {
            var overrideRecs = ParseRecOverrides(ImGui.GetClipboardText());
            if (overrideRecs.Count > Recommendations.Schedules.Count)
                throw new Exception(Loc.Format("Override list is longer than base schedule: {0} > {1}", "覆盖列表比基础排班更长：{0} > {1}", overrideRecs.Count, Recommendations.Schedules.Count));
            OverrideSideRecsLastWorkshop(overrideRecs);
        }
        catch (Exception ex)
        {
            ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message));
        }
    }

    private void OverrideSideRecsLastWorkshopSolver(bool nextWeek)
    {
        EnsureDemandFavorsAvailable();
        _pendingActions.Add(() =>
        {
            OverrideSideRecsLastWorkshop(SolveRecOverrides(nextWeek));
            return true;
        });
    }

    private void OverrideSideRecsLastWorkshop(List<WorkshopSolver.WorkshopRec> overrides)
    {
        foreach (var (r, o) in Recommendations.Schedules.Zip(overrides))
        {
            // if base recs have >1 workshop, remove last (assume we always want to override 4th workshop)
            if (r.Workshops.Count > 1)
                r.Workshops.RemoveAt(r.Workshops.Count - 1);
            // and add current override as a schedule for last workshop
            r.Workshops.Add(o);
        }
        if (overrides.Count > Recommendations.Schedules.Count)
            Service.ChatGui.Print(Loc.Tr("Warning: couldn't fit all overrides into base schedule", "警告：无法将所有覆盖排班完整塞入基础排班"), Plugin.Name);
    }

    private void OverrideSideRecsAsapClipboard()
    {
        try
        {
            var overrideRecs = ParseRecOverrides(ImGui.GetClipboardText());
            if (overrideRecs.Count > Recommendations.Schedules.Count * 4)
                throw new Exception(Loc.Format("Override list is longer than base schedule: {0} > 4 * {1}", "覆盖列表比基础排班更长：{0} > 4 * {1}", overrideRecs.Count, Recommendations.Schedules.Count));
            OverrideSideRecsAsap(overrideRecs);
        }
        catch (Exception ex)
        {
            ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message));
        }
    }

    private void OverrideSideRecsAsapSolver(bool nextWeek)
    {
        EnsureDemandFavorsAvailable();
        _pendingActions.Add(() =>
        {
            OverrideSideRecsAsap(SolveRecOverrides(nextWeek));
            return true;
        });
    }

    private void OverrideSideRecsAsap(List<WorkshopSolver.WorkshopRec> overrides)
    {
        var nextOverride = 0;
        foreach (var r in Recommendations.Schedules)
        {
            var batchSize = Math.Min(4, overrides.Count - nextOverride);
            if (batchSize == 0)
                break; // nothing left to override

            // if base recs have >1 workshop, remove last (assume we always want to override 4th workshop)
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

    private WorkshopSolver.Recs ParseRecs(string str)
    {
        if (LooksLikeChineseDocFormat(str))
            return ParseChineseDocRecs(str);

        return ParseOcRecs(str);
    }

    private WorkshopSolver.Recs ParseOcRecs(string str)
    {
        var result = new WorkshopSolver.Recs();

        var curRec = new WorkshopSolver.DayRec();
        var nextSlot = 24;
        var curCycle = 0;
        foreach (var l in str.Split('\n', '\r'))
        {
            if (TryParseCycleStart(l, out var cycle))
            {
                // complete previous cycle; if the number was not known, assume it is next cycle - 1
                result.Add(curCycle > 0 ? curCycle : cycle - 1, curRec);
                curRec = new();
                nextSlot = 24;
                curCycle = cycle;
            }
            else if (l == "First 3 Workshops" || l == "All Workshops")
            {
                // just a sanity check...
                if (!curRec.Empty)
                    throw new Exception(Loc.Tr("Unexpected start of 1st workshop recs", "第一个工坊推荐排班的起始位置异常"));
            }
            else if (l == "4th Workshop")
            {
                // ensure next item goes into new rec list
                // TODO: do we want to add an extra empty list if this is the first line?..
                nextSlot = 24;
            }
            else if (TryParseItem(l) is var item && item != null)
            {
                if (nextSlot + item.Value.CraftingTime > 24)
                {
                    // start next workshop schedule
                    curRec.Workshops.Add(new());
                    nextSlot = 0;
                }
                curRec.Workshops.Last().Add(nextSlot, item.Value.RowId);
                nextSlot += item.Value.CraftingTime;
            }
            else
                Service.Log.Verbose($"Failed to parse {l}");
        }
        // complete current cycle; if the number was not known, assume it is tomorrow.
        // On the 7th day, importing a rec will assume the next week, but we can't import into the next week so just modulo it to the first week. Theoretically shouldn't cause problems.
        result.Add(curCycle > 0 ? curCycle : (AgentMJICraftSchedule.Instance()->Data->CycleInProgress + 2) % 8, curRec);

        return result;
    }

    private WorkshopSolver.Recs ParseChineseDocRecs(string str)
    {
        var result = new WorkshopSolver.Recs();
        var anyCycle = false;

        foreach (var rawLine in str.Split('\n', '\r'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (!TryParseChineseCycleLine(line, out var cycle, out var payload))
            {
                Service.Log.Verbose($"Failed to parse CN line {line}");
                continue;
            }

            anyCycle = true;
            if (IsChineseRestDay(payload))
            {
                result.Add(cycle, new());
                continue;
            }

            var dayRec = new WorkshopSolver.DayRec();
            var workshopRec = new WorkshopSolver.WorkshopRec();
            dayRec.Workshops.Add(workshopRec);

            var nextSlot = 0;
            foreach (var token in SplitChineseScheduleItems(payload))
            {
                var item = TryParseItem(token) ?? throw new Exception(Loc.Format("Could not match item: {0}", "无法识别道具：{0}", token));
                if (nextSlot + item.CraftingTime > 24)
                    throw new Exception(Loc.Format("Schedule for cycle {0} exceeds 24 hours", "周期 {0} 的排班超过了 24 小时", cycle));

                workshopRec.Add(nextSlot, item.RowId);
                nextSlot += item.CraftingTime;
            }

            if (workshopRec.Slots.Count == 0)
                throw new Exception(Loc.Format("No craft entries found for cycle {0}", "周期 {0} 未找到任何工坊条目", cycle));

            result.Add(cycle, dayRec);
        }

        if (!anyCycle)
            throw new Exception(Loc.Tr("No valid cycle lines were found in the clipboard", "剪贴板里没有识别到有效的周期行"));

        return result;
    }

    private static bool TryParseCycleStart(string str, out int cycle)
    {
        // OC has two formats:
        // - single day recs are 'Season N (mmm dd-dd), Cycle C Recommendations'
        // - multi day recs are 'Season N (mmm dd-dd) Cycle K-L Recommendations' followed by 'Cycle C'
        if (str.StartsWith("Cycle "))
            return int.TryParse(str.AsSpan(6, 1), out cycle);
        else if (str.StartsWith("Season ") && str.IndexOf(", Cycle ") is var cycleStart && cycleStart > 0)
            return int.TryParse(str.AsSpan(cycleStart + 8, 1), out cycle);
        else
        {
            cycle = 0;
            return false;
        }
    }

    private MJICraftworksObject? TryParseItem(string line)
    {
        var matchingRows = _searchAliases
            .Select((aliases, i) => (aliases, i))
            .Where(t => t.aliases.Any(a => !string.IsNullOrEmpty(a) && IsMatch(line, a)))
            .ToList();
        if (matchingRows.Count > 1)
        {
            matchingRows = [.. matchingRows.OrderByDescending(t => MatchingScore(t.aliases, line))];
            Service.Log.Info($"Row '{line}' matches {matchingRows.Count} items: {string.Join(", ", matchingRows.Select(r => _displayNames[r.i]))}\n" +
                "First one is most likely the correct match. Please report if this is wrong.");
        }
        return matchingRows.Count > 0 ? _craftSheet.GetRow((uint)matchingRows.First().i) : null;
    }


    private static bool IsMatch(string line, string alias)
    {
        if (ContainsNonAscii(alias))
            return line.Contains(alias, StringComparison.OrdinalIgnoreCase);

        return Regex.IsMatch(line, $@"\b{Regex.Escape(alias)}\b");
    }

    private static int MatchingScore(IEnumerable<string> aliases, string line)
        => aliases.Where(a => line.Contains(a, StringComparison.OrdinalIgnoreCase)).Select(a => a.Length).DefaultIfEmpty(0).Max();

    private static bool ContainsNonAscii(string text) => text.Any(c => c > 127);

    private List<WorkshopSolver.WorkshopRec> ParseRecOverrides(string str)
    {
        var result = new List<WorkshopSolver.WorkshopRec>();
        var nextSlot = 24;

        foreach (var l in str.Split('\n', '\r'))
        {
            if (l.StartsWith("Schedule #"))
            {
                // ensure next item goes into new rec list
                nextSlot = 24;
            }
            else if (TryParseItem(l) is var item && item != null)
            {
                if (nextSlot + item.Value.CraftingTime > 24)
                {
                    // start next workshop schedule
                    result.Add(new());
                    nextSlot = 0;
                }
                result.Last().Add(nextSlot, item.Value.RowId);
                nextSlot += item.Value.CraftingTime;
            }
            else
                Service.Log.Verbose($"Failed to parse {l}");
        }

        return result;
    }

    private unsafe List<WorkshopSolver.WorkshopRec> SolveRecOverrides(bool nextWeek)
    {
        var mji = MJIManager.Instance();
        if (!mji->IsPlayerInSanctuary) return [];
        var state = new WorkshopSolver.FavorState();
        var offset = nextWeek ? 6 : 3;
        for (var i = 0; i < 3; ++i)
        {
            state.CraftObjectIds[i] = mji->FavorState->CraftObjectIds[i + offset];
            state.CompletedCounts[i] = mji->FavorState->NumDelivered[i + offset] + mji->FavorState->NumScheduled[i + offset];
        }
        if (!mji->DemandDirty)
        {
            state.Popularity.Set(nextWeek ? mji->NextPopularity : mji->CurrentPopularity);
        }

        try
        {
            return new WorkshopSolverFavorSheet(state).Recs;
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
            Service.Log.Error($"Current favors: {state.CraftObjectIds[0]} #{state.CompletedCounts[0]}, {state.CraftObjectIds[1]} #{state.CompletedCounts[1]}, {state.CraftObjectIds[2]} #{state.CompletedCounts[2]}");
            return [];
        }
    }

    public static string OfficialNameToBotName(string name)
    {
        // why do they keep fucking changing this!?
        if (name.StartsWith("Isleworks "))
            return name.Remove(0, 10);
        //if (name.StartsWith("Isleberry "))
        //    return name.Remove(0, 10);
        if (name.StartsWith("Islefish "))
            return name.Remove(0, 9);
        if (name.StartsWith("Island "))
            return name.Remove(0, 7);
        if (name == "Mammet of the Cycle Award")
            return "Mammet Award";
        return name;
    }

    private List<string> BuildSearchAliases(MJICraftworksObject row)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localizedName = row.Item.Value.Name.ExtractText();

        AddAliasVariants(aliases, localizedName);
        AddAliasVariants(aliases, OfficialNameToBotName(localizedName));
        AddAliasVariants(aliases, _botNames[(int)row.RowId]);

        return aliases.OrderByDescending(a => a.Length).ToList();
    }

    private static void AddAliasVariants(HashSet<string> aliases, string name)
    {
        foreach (var alias in ExpandAliases(name))
        {
            var trimmed = alias.Trim();
            if (trimmed.Length > 0)
                aliases.Add(trimmed);
        }
    }

    private static IEnumerable<string> ExpandAliases(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            yield break;

        yield return name;

        foreach (var prefix in new[] { "Isleworks ", "Islefish ", "Island ", "开拓工房", "海岛", "无人岛" })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return name[prefix.Length..];
        }

        if (name.StartsWith("无人岛", StringComparison.Ordinal))
            yield return $"无人{name["无人岛".Length..]}";
    }

    private static bool LooksLikeChineseDocFormat(string text)
        => text.Split('\n', '\r').Any(line => TryParseChineseCycleLine(line.Trim(), out _, out _));

    private static bool TryParseChineseCycleLine(string line, out int cycle, out string payload)
    {
        var match = Regex.Match(line, @"^[Dd](?<cycle>[1-7])\s*[:：]\s*(?<payload>.+)$");
        if (match.Success)
        {
            cycle = int.Parse(match.Groups["cycle"].Value);
            payload = match.Groups["payload"].Value.Trim();
            return true;
        }

        cycle = 0;
        payload = string.Empty;
        return false;
    }

    private static bool IsChineseRestDay(string payload)
        => payload is "休息" or "休息日";

    private static IEnumerable<string> SplitChineseScheduleItems(string payload)
    {
        payload = Regex.Replace(payload.Trim(), @"^\d+\s*[xX×＊*]\s*", string.Empty);
        return payload
            .Split(['、', '，', ',', '；', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);
    }

    private unsafe void EnsureDemandFavorsAvailable()
    {
        if (MJIManager.Instance()->DemandDirty)
        {
            WorkshopUtils.RequestDemandFavors();
            _pendingActions.Add(() => !MJIManager.Instance()->DemandDirty && MJIManager.Instance()->FavorState->UpdateState == 2);
        }
    }

    private unsafe void ApplyRecommendation(int cycle, WorkshopSolver.DayRec rec)
    {
        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();
        foreach (var w in rec.Enumerate(maxWorkshops))
            if (!IgnoreFourthWorkshop || w.workshop < maxWorkshops - 1)
                foreach (var r in w.rec.Slots)
                    WorkshopUtils.ScheduleItemToWorkshop(r.CraftObjectId, r.Slot, cycle, w.workshop);
    }

    private void ApplyRecommendationToCurrentCycle(WorkshopSolver.DayRec rec)
    {
        var cycle = AgentMJICraftSchedule.Instance()->Data->CycleDisplayed;
        ApplyRecommendation(cycle, rec);
        WorkshopUtils.ResetCurrentCycleToRefreshUI();
    }

    private void ApplyRecommendations(bool nextWeek)
    {
        // TODO: clear recs!

        try
        {
            var agentData = AgentMJICraftSchedule.Instance()->Data;
            if (Recommendations.Schedules.Count > 5)
                throw new Exception(Loc.Format("Too many days in recs: {0}", "推荐排班天数过多：{0}", Recommendations.Schedules.Count));

            var forbiddenCycles = nextWeek ? 0 : (1u << (agentData->CycleInProgress + 1)) - 1;
            if ((Recommendations.CyclesMask & forbiddenCycles) != 0)
                throw new Exception(Loc.Tr("Some of the cycles in schedule are already in progress or are done", "排班中的部分周期已经开始或已经结束"));

            var currentRestCycles = nextWeek ? agentData->RestCycles >> 7 : agentData->RestCycles & 0x7F;
            if ((currentRestCycles & Recommendations.CyclesMask) != 0)
            {
                // we need to change rest cycles - set to C1 and last unused
                var freeCycles = ~Recommendations.CyclesMask & 0x7F;
                if ((freeCycles & 1) == 0)
                    throw new Exception(Loc.Tr("Sorry, we assume C1 is always rest - set rest days manually to match your schedule", "当前逻辑默认 C1 必须是休息日，请手动调整休息日后再应用该排班"));
                var rest = (1u << (31 - BitOperations.LeadingZeroCount(freeCycles))) | 1;
                if (BitOperations.PopCount(rest) != 2)
                    throw new Exception(Loc.Tr("Something went wrong, failed to determine rest days", "发生异常，无法确定休息日"));

                var changedRest = rest ^ currentRestCycles;
                if ((changedRest & forbiddenCycles) != 0)
                    throw new Exception(Loc.Tr("Can't apply this schedule: it would require changing rest days for cycles that are in progress or already done", "无法应用该排班：这会修改已经开始或已经结束周期的休息日"));

                var newRest = nextWeek ? (rest << 7) | (agentData->RestCycles & 0x7F) : (agentData->RestCycles & 0x3F80) | rest;
                WorkshopUtils.SetRestCycles(newRest);
            }

            var cycle = agentData->CycleDisplayed;
            foreach (var (c, r) in Recommendations.Enumerate())
                ApplyRecommendation(c - 1 + (nextWeek ? 7 : 0), r);
            WorkshopUtils.ResetCurrentCycleToRefreshUI();
        }
        catch (Exception ex)
        {
            ReportError(Loc.Format("Error: {0}", "错误：{0}", ex.Message));
        }
    }

    private static void ReportError(string msg, bool silent = false)
    {
        Service.Log.Error(msg);
        if (!silent)
            Service.ChatGui.PrintError(msg);
    }
}
