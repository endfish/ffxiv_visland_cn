using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using visland.Helpers;

namespace visland.Workshop;

internal class ScheduleApplier {
    public bool IgnoreFourthWorkshop { get; set; }

    public unsafe int ApplyRecommendation(int cycle, WorkshopSolver.DayRec rec, int minStartingHour = 0) {
        var maxWorkshops = WorkshopUtils.GetMaxWorkshops();
        var scheduled = 0;
        foreach (var w in rec.Enumerate(maxWorkshops))
            if (!IgnoreFourthWorkshop || w.workshop < maxWorkshops - 1)
                foreach (var r in w.rec.Slots) {
                    if (r.Slot < minStartingHour)
                        continue;
                    WorkshopUtils.ScheduleItemToWorkshop(r.CraftObjectId, r.Slot, cycle, w.workshop);
                    scheduled++;
                }
        return scheduled;
    }

    public unsafe void ApplyRecommendationToCurrentCycle(WorkshopSolver.DayRec rec) {
        var agentData = AgentMJICraftSchedule.Instance()->Data;
        var cycle = agentData->CycleDisplayed;
        var minHour = cycle == agentData->CycleInProgress ? agentData->HourSinceCycleStart : 0;
        ApplyRecommendation(cycle, rec, minHour);
        WorkshopUtils.ResetCurrentCycleToRefreshUI();
    }

    public unsafe void ApplyRecommendations(WorkshopSolver.Recs recommendations, bool nextWeek) {
        var agentData = AgentMJICraftSchedule.Instance()->Data;
        var restDaysCount = BitOperations.PopCount(~recommendations.CyclesMask & 0x7F);
        if (recommendations.Schedules.Count + restDaysCount > 7)
            throw new Exception(Loc.Format(
                "Too many days in recs: {0} crafts + {1} rest > 7",
                "推荐排班天数过多：{0} 个生产日 + {1} 个休息日 > 7",
                recommendations.Schedules.Count,
                restDaysCount));

        var cycleInProgress = nextWeek ? -1 : agentData->CycleInProgress;
        var hourSinceStart = nextWeek ? 0 : agentData->HourSinceCycleStart;
        var completedCycles = cycleInProgress > 0 ? (1u << cycleInProgress) - 1 : 0u;
        var skippedMask = recommendations.CyclesMask & completedCycles;
        if (skippedMask != 0) {
            var skipped = FormatCycleMask(skippedMask);
            Service.Log.Info($"Skipping completed cycles: {skipped}");
            Service.ChatGui.Print(Loc.Format("Skipping completed cycles: {0}", "已跳过完成的周期：{0}", skipped), Plugin.Name);
        }

        var hasApplicable = false;
        foreach ((var c, var r) in recommendations.Enumerate()) {
            if ((completedCycles & (1u << (c - 1))) != 0)
                continue;
            if (c - 1 == cycleInProgress)
                hasApplicable |= r.Workshops.Any(w => w.Slots.Any(s => s.Slot >= hourSinceStart));
            else
                hasApplicable = true;
        }
        if (!hasApplicable)
            throw new Exception(Loc.Tr(
                "No remaining cycles to apply — the whole schedule is already done or in progress",
                "没有可应用的剩余周期——整份排班均已完成或正在进行"));

        var currentRestCycles = nextWeek ? agentData->RestCycles >> 7 : agentData->RestCycles & 0x7F;
        if ((currentRestCycles & recommendations.CyclesMask) != 0) {
            var freeCycles = ~recommendations.CyclesMask & 0x7F;
            if ((freeCycles & 1) == 0)
                throw new Exception(Loc.Tr(
                    "Sorry, we assume C1 is always rest - set rest days manually to match your schedule",
                    "当前逻辑默认 C1 必须是休息日，请手动调整休息日后再应用该排班"));

            uint rest;
            if (BitOperations.PopCount(freeCycles) == 1) {
                rest = freeCycles;
            }
            else {
                rest = (1u << (31 - BitOperations.LeadingZeroCount(freeCycles))) | 1;
                if (BitOperations.PopCount(rest) != 2)
                    throw new Exception(Loc.Tr("Something went wrong, failed to determine rest days", "发生异常，无法确定休息日"));
            }

            var changedRest = rest ^ currentRestCycles;
            if ((changedRest & completedCycles) != 0) {
                Service.Log.Warning("Skipping rest-day adjustment: would affect cycles already done or in progress");
                Service.ChatGui.Print(Loc.Tr(
                    "Skipping rest-day adjustment for this week — set rest days manually if needed",
                    "已跳过本周休息日调整——如有需要，请手动设置休息日"), Plugin.Name);
            }
            else {
                var newRest = nextWeek ? (rest << 7) | (agentData->RestCycles & 0x7F) : (agentData->RestCycles & 0x3F80) | rest;
                WorkshopUtils.SetRestCycles(newRest);
            }
        }

        var appliedCycles = 0;
        var appliedSlots = 0;
        foreach ((var c, var r) in recommendations.Enumerate()) {
            if ((completedCycles & (1u << (c - 1))) != 0)
                continue;
            var minHour = c - 1 == cycleInProgress ? hourSinceStart : 0;
            var scheduled = ApplyRecommendation(c - 1 + (nextWeek ? 7 : 0), r, minHour);
            if (scheduled > 0) {
                appliedCycles++;
                appliedSlots += scheduled;
            }
            else if (c - 1 == cycleInProgress && minHour > 0)
                Service.Log.Info($"Cycle {c}: no remaining slots after hour {minHour}");
        }

        if (appliedSlots == 0)
            throw new Exception(Loc.Tr("No cycles were applied", "没有成功应用任何周期"));

        WorkshopUtils.ResetCurrentCycleToRefreshUI();
        if (skippedMask != 0 || cycleInProgress >= 0 && hourSinceStart > 0)
            Service.ChatGui.Print(Loc.Format(
                "Applied {0} craft(s) across {1} cycle(s)",
                "已在 {1} 个周期中应用 {0} 个生产条目",
                appliedSlots,
                appliedCycles), Plugin.Name);
    }

    public static string FormatCycleMask(uint mask) {
        var cycles = new List<int>();
        for (var c = 1; c <= 7; ++c) {
            if ((mask & (1u << (c - 1))) != 0)
                cycles.Add(c);
        }
        return string.Join(", ", cycles.Select(c => $"C{c}"));
    }
}
