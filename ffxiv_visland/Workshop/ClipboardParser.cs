using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using visland.Helpers;

namespace visland.Workshop;

internal unsafe class ClipboardParser {
    private readonly ExcelSheet<MJICraftworksObject> _craftSheet;
    private readonly List<string> _displayNames;
    private readonly List<string> _botNames;
    private readonly List<List<string>> _searchAliases;

    public ClipboardParser(ExcelSheet<MJICraftworksObject> craftSheet, List<string> botNames) {
        _craftSheet = craftSheet;
        _botNames = botNames;
        _displayNames = [.. craftSheet.Select(r => r.Item.Value.Name.ToString())];
        _searchAliases = [.. craftSheet.Select(BuildSearchAliases)];
    }

    public WorkshopSolver.Recs ParseRecs(string str) {
        if (LooksLikeChineseDocFormat(str))
            return ParseChineseDocRecs(str);

        return ParseOcRecs(str);
    }

    private WorkshopSolver.Recs ParseOcRecs(string str) {
        var result = new WorkshopSolver.Recs();

        var curRec = new WorkshopSolver.DayRec();
        var nextSlot = 24;
        var curCycle = 0;
        foreach (var l in str.Split('\n', '\r')) {
            if (TryParseCycleStart(l, out var cycle)) {
                result.Add(curCycle > 0 ? curCycle : cycle - 1, curRec);
                curRec = new();
                nextSlot = 24;
                curCycle = cycle;
            }
            else if (l is "First 3 Workshops" or "All Workshops") {
                if (!curRec.Empty)
                    throw new Exception(Loc.Tr("Unexpected start of 1st workshop recs", "第一个工坊推荐排班的起始位置异常"));
            }
            else if (l == "4th Workshop") {
                nextSlot = 24;
            }
            else if (TryParseItem(l) is var item && item != null) {
                if (nextSlot + item.Value.CraftingTime > 24) {
                    curRec.Workshops.Add(new());
                    nextSlot = 0;
                }
                curRec.Workshops.Last().Add(nextSlot, item.Value.RowId);
                nextSlot += item.Value.CraftingTime;
            }
            else
                Service.Log.Verbose($"Failed to parse {l}");
        }
        result.Add(curCycle > 0 ? curCycle : (AgentMJICraftSchedule.Instance()->Data->CycleInProgress + 2) % 8, curRec);

        return result;
    }

    private WorkshopSolver.Recs ParseChineseDocRecs(string str) {
        var result = new WorkshopSolver.Recs();
        var anyCycle = false;

        foreach (var rawLine in str.Split('\n', '\r')) {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (!TryParseChineseCycleLine(line, out var cycle, out var payload)) {
                Service.Log.Verbose($"Failed to parse CN line {line}");
                continue;
            }

            anyCycle = true;
            if (IsChineseRestDay(payload)) {
                result.Add(cycle, new());
                continue;
            }

            var dayRec = new WorkshopSolver.DayRec();
            var workshopRec = new WorkshopSolver.WorkshopRec();
            dayRec.Workshops.Add(workshopRec);

            var nextSlot = 0;
            foreach (var token in SplitChineseScheduleItems(payload)) {
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

    public List<WorkshopSolver.WorkshopRec> ParseRecOverrides(string str) {
        var result = new List<WorkshopSolver.WorkshopRec>();
        var nextSlot = 24;

        foreach (var l in str.Split('\n', '\r')) {
            if (l.StartsWith("Schedule #")) {
                nextSlot = 24;
            }
            else if (TryParseItem(l) is var item && item != null) {
                if (nextSlot + item.Value.CraftingTime > 24) {
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

    private static bool TryParseCycleStart(string str, out int cycle) {
        if (str.StartsWith("Cycle "))
            return int.TryParse(str.AsSpan(6, 1), out cycle);
        if (str.StartsWith("Season ") && str.IndexOf(", Cycle ") is var cycleStart && cycleStart > 0)
            return int.TryParse(str.AsSpan(cycleStart + 8, 1), out cycle);
        cycle = 0;
        return false;
    }

    private MJICraftworksObject? TryParseItem(string line) {
        var matchingRows = _searchAliases
            .Select((aliases, i) => (aliases, i))
            .Where(t => t.aliases.Any(a => !string.IsNullOrEmpty(a) && IsMatch(line, a)))
            .ToList();
        if (matchingRows.Count > 1) {
            matchingRows = [.. matchingRows.OrderByDescending(t => MatchingScore(t.aliases, line))];
            Service.Log.Info($"Row '{line}' matches {matchingRows.Count} items: {string.Join(", ", matchingRows.Select(r => _displayNames[r.i]))}\n" +
                "First one is most likely the correct match. Please report if this is wrong.");
        }
        return matchingRows.Count > 0 ? _craftSheet.GetRow((uint)matchingRows.First().i) : null;
    }

    private static bool IsMatch(string line, string alias) {
        if (ContainsNonAscii(alias))
            return line.Contains(alias, StringComparison.OrdinalIgnoreCase);

        return Regex.IsMatch(line, $@"\b{Regex.Escape(alias)}\b");
    }

    private static int MatchingScore(IEnumerable<string> aliases, string line)
        => aliases.Where(a => line.Contains(a, StringComparison.OrdinalIgnoreCase)).Select(a => a.Length).DefaultIfEmpty(0).Max();

    private static bool ContainsNonAscii(string text) => text.Any(c => c > 127);

    private List<string> BuildSearchAliases(MJICraftworksObject row) {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localizedName = row.Item.Value.Name.ToString();

        AddAliasVariants(aliases, localizedName);
        AddAliasVariants(aliases, OSCHandler.OfficialNameToBotName(localizedName));
        AddAliasVariants(aliases, _botNames[(int)row.RowId]);

        return [.. aliases.OrderByDescending(a => a.Length)];
    }

    private static void AddAliasVariants(HashSet<string> aliases, string name) {
        foreach (var alias in ExpandAliases(name)) {
            var trimmed = alias.Trim();
            if (trimmed.Length > 0)
                aliases.Add(trimmed);
        }
    }

    private static IEnumerable<string> ExpandAliases(string name) {
        if (string.IsNullOrWhiteSpace(name))
            yield break;

        yield return name;

        foreach (var prefix in new[] { "Isleworks ", "Islefish ", "Island ", "开拓工房", "海岛", "无人岛" }) {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                yield return name[prefix.Length..];
        }

        if (name.StartsWith("无人岛", StringComparison.Ordinal))
            yield return $"无人{name["无人岛".Length..]}";
    }

    private static bool LooksLikeChineseDocFormat(string text)
        => text.Split('\n', '\r').Any(line => TryParseChineseCycleLine(line.Trim(), out _, out _));

    private static bool TryParseChineseCycleLine(string line, out int cycle, out string payload) {
        var match = Regex.Match(line, @"^[Dd](?<cycle>[1-7])\s*[:：]\s*(?<payload>.+)$");
        if (match.Success) {
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

    private static IEnumerable<string> SplitChineseScheduleItems(string payload) {
        payload = Regex.Replace(payload.Trim(), @"^\d+\s*[xX×＊*]\s*", string.Empty);
        return payload
            .Split(['、', '，', ',', '；', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0);
    }
}
