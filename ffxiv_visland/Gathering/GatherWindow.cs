using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using ECommons.SimpleGui;
using Dalamud.Bindings.ImGui;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using visland.Helpers;
using visland.IPC;
using static visland.Gathering.GatherRouteDB;
using ECommons.GameHelpers;

namespace visland.Gathering;

public class GatherWindow : Window, IDisposable
{
    private readonly UITree _tree = new();
    private readonly List<System.Action> _postDraw = [];

    public GatherRouteDB RouteDB = null!;
    public GatherRouteExec Exec = new();
    public GatherDebug _debug = null!;

    private int selectedRouteIndex = -1;
    public static bool loop;

    private readonly List<uint> Colours = GenericHelpers.GetSheet<UIColor>()!.Select(x => x.Dark).ToList();
    private Vector4 greenColor = new Vector4(0x5C, 0xB8, 0x5C, 0xFF) / 0xFF;
    private Vector4 redColor = new Vector4(0xD9, 0x53, 0x4F, 0xFF) / 0xFF;
    private Vector4 yellowColor = new Vector4(0xD9, 0xD9, 0x53, 0xFF) / 0xFF;

    private readonly List<int> Items = GenericHelpers.GetSheet<Item>()?.Select(x => (int)x.RowId).ToList()!;
    private ExcelSheet<Item> _items = null!;

    private string searchString = string.Empty;
    private readonly List<Route> FilteredRoutes = [];
    private FontAwesomeIcon PlayIcon => Exec.CurrentRoute != null && !Exec.Paused ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play;
    private string PlayTooltip => Exec.CurrentRoute == null
        ? Loc.Tr("Start Route", "开始路线")
        : Exec.Paused
            ? Loc.Tr("Resume Route", "继续路线")
            : Loc.Tr("Pause Route", "暂停路线");

    public GatherWindow() : base(Loc.Tr("Gathering Automation", "采集自动化"), ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size = new Vector2(800, 800);
        SizeCondition = ImGuiCond.FirstUseEver;
        RouteDB = Service.Config.Get<GatherRouteDB>();

        _debug = new(Exec);
        _items = GenericHelpers.GetSheet<Item>()!;
    }

    public void Setup()
    {
        EzConfigGui.Window?.Size = new Vector2(800, 800);
        EzConfigGui.Window?.SizeCondition = ImGuiCond.FirstUseEver;
        RouteDB = Service.Config.Get<GatherRouteDB>();

        _debug = new(Exec);
        _items = GenericHelpers.GetSheet<Item>()!;
    }

    public void Dispose() => Exec.Dispose();

    public override void PreOpenCheck() => Exec.Update();

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs)
        {
            using (var tab = ImRaii.TabItem(Loc.Tr("Routes", "路线")))
                if (tab)
                {
                    DrawExecution();
                    ImGui.Separator();
                    ImGui.Spacing();

                    var cra = ImGui.GetContentRegionAvail();
                    var sidebar = cra with { X = cra.X * 0.40f };
                    var editor = cra with { X = cra.X * 0.60f };

                    DrawSidebar(sidebar);
                    ImGui.SameLine();
                    DrawEditor(editor);

                    foreach (var a in _postDraw)
                        a();
                    _postDraw.Clear();
                }
            using (var tab = ImRaii.TabItem(Loc.Tr("Log", "日志")))
                if (tab)
                    InternalLog.PrintImgui();
            using (var tab = ImRaii.TabItem(Loc.Tr("Debug", "调试")))
                if (tab)
                    _debug.Draw();
        }
    }

    private void DrawExecution()
    {
        ImGuiEx.Text(Loc.Tr("Status: ", "状态："));
        ImGui.SameLine();

        if (Exec.CurrentRoute != null)
            Utils.FlashText(Exec.Paused ? Loc.Tr("PAUSED", "已暂停") : Exec.Waiting ? Loc.Tr("WAITING", "等待中") : Loc.Tr("RUNNING", "运行中"), new Vector4(1.0f, 1.0f, 1.0f, 1.0f), Exec.Paused ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f), 2);
        ImGui.SameLine();

        if (Exec.CurrentRoute == null || Exec.CurrentWaypoint >= Exec.CurrentRoute.Waypoints.Count)
        {
            ImGui.TextUnformatted(Loc.Tr("Route not running", "当前没有运行路线"));
            return;
        }

        if (Exec.CurrentRoute != null) // Finish() call could've reset it
        {
            ImGui.SameLine();
            ImGuiEx.Text(Loc.Format("{0}: Step #{1} {2}", "{0}：步骤 #{1} {2}", Exec.CurrentRoute.Name, Exec.CurrentWaypoint + 1, Exec.CurrentRoute.Waypoints[Exec.CurrentWaypoint].Position));

            if (Exec.Waiting)
            {
                ImGui.SameLine();
                ImGuiEx.Text(Loc.Format("waiting {0}ms", "等待 {0} 毫秒", Exec.WaitUntil - System.Environment.TickCount64));
            }
        }

        ImGui.SameLine();
        ImGuiEx.Text(Loc.Format("State: {0}", "阶段：{0}", UICombo.EnumString(Exec.CurrentState)));
    }

    private unsafe void DrawSidebar(Vector2 size)
    {
        using (ImRaii.Child("Sidebar", size, false))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            {
                RouteDB.Routes.Add(new() { Name = Loc.Tr("Unnamed Route", "未命名路线") });
                RouteDB.NotifyModified();
            }

            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Tr("Create a New Route", "创建新路线"));
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileImport))
                TryImport(RouteDB);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.Tr("Import Route from Clipboard", "从剪贴板导入路线"));

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
                ImGui.OpenPopup("Advanced Options");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Loc.Tr("Advanced Options", "高级选项"));
            DrawRouteSettingsPopup();

            ImGui.SameLine();
            RapidImport();

            ImGuiEx.TextV(Loc.Tr("Search: ", "搜索："));
            ImGui.SameLine();
            ImGuiEx.SetNextItemFullWidth();
            if (ImGui.InputText("###RouteSearch", ref searchString, 500))
            {
                FilteredRoutes.Clear();
                if (searchString.Length > 0)
                {
                    foreach (var route in RouteDB.Routes)
                    {
                        if (route.Name.Contains(searchString, System.StringComparison.CurrentCultureIgnoreCase) || route.Group.Contains(searchString, System.StringComparison.CurrentCultureIgnoreCase))
                            FilteredRoutes.Add(route);
                    }
                }
            }

            ImGui.Separator();

            using (ImRaii.Child("routes"))
            {
                var groups = GetGroups(RouteDB, true);
                foreach (var group in groups)
                {
                    foreach (var _ in _tree.Node($"{DisplayGroup(group)}###{groups.IndexOf(group)}", contextMenu: () => ContextMenuGroup(group)))
                    {
                        var routeSource = FilteredRoutes.Count > 0 ? FilteredRoutes : RouteDB.Routes;
                        for (var i = 0; i < routeSource.Count; i++)
                        {
                            var route = routeSource[i];
                            var routeGroup = string.IsNullOrEmpty(route.Group) ? "None" : route.Group;
                            if (routeGroup == group)
                            {
                                if (ImGui.Selectable($"{route.Name} ({route.Waypoints.Count} {Loc.Tr("steps", "步")})###{i}", i == selectedRouteIndex))
                                    selectedRouteIndex = i;
                                //if (ImRaii.ContextPopup($"{route.Name}{i}"))
                                //{
                                //    selectedRouteIndex = i;
                                //    ContextMenuRoute(routeSource[i]);
                                //}
                            }
                        }
                    }
                }
            }
        }
    }

    internal static bool RapidImportEnabled = false;
    private void RapidImport()
    {
        if (ImGui.Checkbox(Loc.Tr("Enable Rapid Import", "启用快速导入"), ref RapidImportEnabled))
            ImGui.SetClipboardText("");

        ImGuiComponents.HelpMarker(Loc.Tr("Import multiple presets with ease by simply copying them. Visland will read your clipboard and attempt to import whatever you copy. Your clipboard will be cleared upon enabling.", "启用后可通过连续复制来快速导入多个预设。Visland 会自动读取你的剪贴板并尝试导入复制的内容。启用时会先清空剪贴板。"));
        if (RapidImportEnabled)
        {
            try
            {
                var text = ImGui.GetClipboardText();
                if (text != "")
                {
                    TryImport(RouteDB);
                    ImGui.SetClipboardText("");
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e.Message, e);
            }
        }
    }

    private void DrawRouteSettingsPopup()
    {
        using var popup = ImRaii.Popup("Advanced Options");
        if (popup.Success)
        {
            Utils.DrawSection(Loc.Tr("Global Route Editing Options", "全局路线编辑选项"), ImGuiColors.ParsedGold);
            if (ImGui.SliderFloat(Loc.Tr("Default Waypoint Radius", "默认路点半径"), ref RouteDB.DefaultWaypointRadius, 0, 100))
                RouteDB.NotifyModified();
            if (ImGui.SliderFloat(Loc.Tr("Default Interaction Radius", "默认交互半径"), ref RouteDB.DefaultInteractionRadius, 0, 100))
                RouteDB.NotifyModified();

            Utils.DrawSection(Loc.Tr("Global Route Operation Options", "全局路线运行选项"), ImGuiColors.ParsedGold);

            if (ImGui.Checkbox(Loc.Tr("Auto Enable Island Sanctuary Gather Mode", "自动开启无人岛采集模式"), ref RouteDB.GatherModeOnStart))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker(Loc.Tr("Enables \"Gather Mode\" when on your Island Sanctuary automatically when commencing a route.", "在无人岛开始执行路线时自动开启“采集模式”。"));

            using (ImRaii.Disabled())
            {
                if (ImGui.Checkbox(Loc.Tr("Stop Route on Error", "发生错误时停止路线"), ref RouteDB.DisableOnErrors))
                    RouteDB.NotifyModified();
            }
            ImGuiComponents.HelpMarker(Loc.Tr("Stops executing a route when you encounter a node you can't gather from due to full inventory.", "当背包已满导致无法采集时，自动停止当前路线。"));

            if (ImGui.Checkbox(Loc.Tr("Teleport between zones", "跨区域时自动传送"), ref RouteDB.TeleportBetweenZones))
                RouteDB.NotifyModified();

            Utils.WorkInProgressIcon();
            ImGui.SameLine();
            if (ImGui.Checkbox(Loc.Tr("Auto Gather", "自动采集"), ref RouteDB.AutoGather))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker(Loc.Tr("Applies to non-island routes only. Will auto gather the item in the \"Item Target\" field and use the best actions available.", "仅适用于非无人岛路线。会自动采集“目标物品”里指定的物品，并使用当前可用的最佳技能。"));

            //if (ImGui.SliderInt("Land Distance", ref RouteDB.LandDistance, 1, 30))
            //    RouteDB.NotifyModified();
            //ImGuiComponents.HelpMarker("Only applies to waypoints auto generated from node scanning. How far to land from the node to land and switch from fly pathfinding to ground pathfinding.");

            Utils.DrawSection(Loc.Tr("Global Route Extras", "全局路线附加功能"), ImGuiColors.ParsedGold);

            if (ImGui.Checkbox(Loc.Tr("Extract materia during routes", "路线中自动精制魔晶石"), ref RouteDB.ExtractMateria))
                RouteDB.NotifyModified();
            if (ImGui.Checkbox(Loc.Tr("Repair gear during routes", "路线中自动修理装备"), ref RouteDB.RepairGear))
                RouteDB.NotifyModified();
            if (ImGui.SliderFloat(Loc.Tr("Repair percentage threshold", "修理耐久阈值"), ref RouteDB.RepairPercent, 0, 100))
                RouteDB.NotifyModified();
            if (ImGui.Checkbox(Loc.Tr("Purify collectables during routes", "路线中自动精选"), ref RouteDB.PurifyCollectables))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker($"Also known as {GenericHelpers.GetRow<Addon>(2160)!.Value.Text}");
            if (ImGui.Checkbox(Loc.Tr("Check AutoRetainer during routes", "路线中检查 AutoRetainer"), ref RouteDB.AutoRetainerIntegration))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker(Loc.Tr("Will enable multi mode when you have any retainers or submarines returned across any enabled characters. Requires the current character to be set as the Preferred Character and the Teleport to FC config enabled in AutoRetainer.", "当任意启用角色有雇员或潜水艇回归时，会自动启用 AutoRetainer 的多角色模式。要求当前角色被设为 Preferred Character，并在 AutoRetainer 中启用传送回部队房屋配置。"));
            if (ImGuiEx.ExcelSheetCombo("##Foods", out Item i, _ => $"[{RouteDB.GlobalFood}] {GenericHelpers.GetRow<Item>((uint)RouteDB.GlobalFood)?.Name}", x => $"[{x.RowId}] {x.Name}", x => x.ItemUICategory.RowId == 46))
            {
                RouteDB.GlobalFood = (int)i.RowId;
                RouteDB.NotifyModified();
            }
            if (RouteDB.GlobalFood != 0)
            {
                ImGui.SameLine();
                if (ImGuiEx.IconButton(FontAwesomeIcon.Undo, "ClearGlobalFood"))
                {
                    RouteDB.GlobalFood = 0;
                    RouteDB.NotifyModified();
                }
            }
            ImGuiComponents.HelpMarker(Loc.Tr("Food set here will apply to all routes unless overwritten in the route itself.", "这里设置的食物会应用到所有路线，除非该路线内单独覆盖。"));
        }
    }

    private void DrawEditor(Vector2 size)
    {
        if (selectedRouteIndex == -1) return;

        var routeSource = FilteredRoutes.Count > 0 ? FilteredRoutes : RouteDB.Routes;
        if (routeSource.Count == 0) return;
        var route = selectedRouteIndex >= routeSource.Count ? routeSource.Last() : routeSource[selectedRouteIndex];

        using (ImRaii.Child("Editor", size))
        {
            if (ImGuiComponents.IconButton(PlayIcon))
            {
                if (Exec.CurrentRoute != null)
                    Exec.Paused = !Exec.Paused;
                if (Exec.CurrentRoute == null && route.Waypoints.Count > 0)
                    Exec.Start(route, 0, true, loop, route.Waypoints[0].Pathfind);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(PlayTooltip);
            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button, loop ? greenColor : redColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, loop ? greenColor : redColor);
            if (ImGuiComponents.IconButton(FontAwesomeIcon.SyncAlt))
                loop ^= true;
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Tr("Loop Route", "循环路线"));
            ImGui.SameLine();

            if (Exec.CurrentRoute != null)
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Stop))
                    Exec.Finish();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Tr("Stop Route", "停止路线"));
                ImGui.SameLine();
            }

            var canDelete = !ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(canDelete))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
                {
                    if (Exec.CurrentRoute == route)
                        Exec.Finish();
                    RouteDB.Routes.Remove(route);
                    RouteDB.NotifyModified();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Loc.Tr("Delete Route (Hold CTRL)", "删除路线（按住 CTRL）"));
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileExport))
            {
                ImGui.SetClipboardText(JsonConvert.SerializeObject(route));
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.Tr("Export Route (\uE052 Base64)", "导出路线（右键复制 Base64）"));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.SetClipboardText(Utils.ToCompressedBase64(route));
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.EllipsisH))
                ImGui.OpenPopup("##MassEditing");
            DrawMassEditContextMenu(route);

            var name = route.Name;
            var group = route.Group;
            var movementType = Service.Condition[ConditionFlag.InFlight] ? Movement.MountFly : Service.Condition[ConditionFlag.Mounted] ? Movement.MountNoFly : Movement.Normal;
            ImGuiEx.TextV(Loc.Tr("Name: ", "名称："));
            ImGui.SameLine();
            if (ImGui.InputText("##name", ref name, 256))
            {
                route.Name = name;
                RouteDB.NotifyModified();
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            {
                Exec.Finish();
                var player = Service.ObjectTable.LocalPlayer;
                if (player != null)
                {
                    route.Waypoints.Add(new() { Position = player.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType });
                    RouteDB.NotifyModified();
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Tr("Add Waypoint: Current Position", "添加路点：当前位置"));
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus))
            {
                var target = Service.TargetManager.Target;
                if (target != null)
                {
                    route.Waypoints.Add(new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType, InteractWithOID = target.BaseId, InteractWithName = target.Name.ToString().ToLower() });
                    RouteDB.NotifyModified();
                    Exec.Start(route, route.Waypoints.Count - 1, false, false);
                }
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Tr("Add Waypoint: Interact with Target", "添加路点：与当前目标交互"));

            ImGuiEx.TextV(Loc.Tr("Group: ", "分组："));
            ImGui.SameLine();
            if (ImGui.InputText("##group", ref group, 256))
            {
                route.Group = group;
                RouteDB.NotifyModified();
            }

            if (RouteDB.AutoGather)
            {
                ImGuiEx.TextV(Loc.Tr("Item Target: ", "目标物品："));
                ImGui.SameLine();
                if (ImGuiEx.ExcelSheetCombo("##Gatherables", out GatheringItem gatherable, _ => $"[{route.TargetGatherItem}] {GenericHelpers.GetRow<Item>((uint)route.TargetGatherItem)?.Name.ToString()}", x => $"[{x.RowId}] {GenericHelpers.GetRow<Item>(x.Item.RowId)?.Name.ToString()}", x => x.Item.RowId != 0))
                {
                    route.TargetGatherItem = (int)gatherable.Item.RowId;
                    RouteDB.NotifyModified();
                }
                if (route.TargetGatherItem != 0)
                {
                    ImGui.SameLine();
                    if (ImGuiEx.IconButton(FontAwesomeIcon.Undo, "ClearItemTarget"))
                    {
                        route.TargetGatherItem = 0;
                        RouteDB.NotifyModified();
                    }
                }
            }

            using (ImRaii.Child("waypoints"))
            {
                for (var i = 0; i < route.Waypoints.Count; ++i)
                {
                    var wp = route.Waypoints[i];
                    foreach (var wn in _tree.Node($"#{i + 1}: [x: {wp.Position.X:f0}, y: {wp.Position.Y:f0}, z: {wp.Position.Z:f0}] ({wp.Movement}) {(wp.InteractWithOID != 0 ? $" @ {wp.InteractWithName} ({wp.InteractWithOID:X})" : "")}###{i}", color: wp.IsPhantom ? ImGuiColors.HealerGreen.ToHex() : 0xffffffff, contextMenu: () => ContextMenuWaypoint(route, i)))
                        DrawWaypoint(wp);
                }
            }
        }
    }


    private bool pathfind;
    private int zoneID;
    private float radius;
    private InteractionType interaction;
    private void DrawMassEditContextMenu(Route route)
    {
        using var popup = ImRaii.Popup("##MassEditing");
        if (!popup) return;

        Utils.DrawSection(Loc.Tr("Route Settings", "路线设置"), ImGuiColors.ParsedGold);
        if (ImGuiEx.ExcelSheetCombo("##Foods", out Item i, _ => $"[{route.Food}] {GenericHelpers.GetRow<Item>((uint)route.Food)?.Name}", x => $"[{x.RowId}] {x.Name}", x => x.ItemUICategory.RowId == 46))
        {
            route.Food = (int)i.RowId;
            RouteDB.NotifyModified();
        }
        if (RouteDB.GlobalFood != 0)
        {
            ImGui.SameLine();
            if (ImGuiEx.IconButton(FontAwesomeIcon.Undo, "ClearLocalFood"))
            {
                route.Food = 0;
                RouteDB.NotifyModified();
            }
        }
        ImGuiComponents.HelpMarker(Loc.Tr("Food set here will apply to this route only and overrides the global food setting.", "这里设置的食物只会应用于当前路线，并覆盖全局食物设置。"));

        Utils.DrawSection(Loc.Tr("Mass Editing", "批量编辑"), ImGuiColors.ParsedGold);
        ImGui.Checkbox(Loc.Tr("Pathfind", "路径规划"), ref pathfind);
        ImGui.SameLine();
        if (ImGui.Button($"{Loc.Tr("Apply All", "全部应用")}###Pathfind"))
        {
            route?.Waypoints.ForEach(x => x.Pathfind = pathfind);
            RouteDB.NotifyModified();
        }

        ImGui.InputInt(Loc.Tr("Zone", "区域"), ref zoneID);
        ImGui.SameLine();
        if (ImGui.Button($"{Loc.Tr("Apply All", "全部应用")}###Zone"))
        {
            route?.Waypoints.ForEach(x => x.ZoneID = zoneID);
            RouteDB.NotifyModified();
        }

        ImGui.InputFloat(Loc.Tr("Radius", "半径"), ref radius);
        ImGui.SameLine();
        if (ImGui.Button($"{Loc.Tr("Apply All", "全部应用")}###Radius"))
        {
            route?.Waypoints.ForEach(x => x.Radius = radius);
            RouteDB.NotifyModified();
        }

        UICombo.Enum(Loc.Tr("Interaction type", "交互类型"), ref interaction);
        ImGui.SameLine();
        if (ImGui.Button($"{Loc.Tr("Apply All", "全部应用")}###Interaction"))
        {
            route?.Waypoints.ForEach(x => x.Interaction = interaction);
            RouteDB.NotifyModified();
        }
    }

    private void DrawWaypoint(Waypoint wp)
    {
        if (ImGuiEx.IconButton(FontAwesomeIcon.MapMarker) && Player.Available)
        {
            wp.Position = Player.Position;
            wp.ZoneID = Service.ClientState.TerritoryType;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Tr("Set Position to Current", "将位置设为当前位置"));
        ImGui.SameLine();
        if (ImGui.InputFloat3(Loc.Tr("Position", "位置"), ref wp.Position))
            RouteDB.NotifyModified();

        if (ImGui.InputInt(Loc.Tr("Zone ID", "区域 ID"), ref wp.ZoneID))
            RouteDB.NotifyModified();

        if (ImGui.InputFloat(Loc.Tr("Radius (yalms)", "半径（码）"), ref wp.Radius))
            RouteDB.NotifyModified();

        if (UICombo.Enum(Loc.Tr("Movement mode", "移动模式"), ref wp.Movement))
            RouteDB.NotifyModified();

        ImGui.SameLine();
        using (var noNav = ImRaii.Disabled(!Utils.HasPlugin(NavmeshIPC.Name)))
        {
            if (ImGui.Checkbox(Loc.Tr("Pathfind?", "启用路径规划？"), ref wp.Pathfind))
                RouteDB.NotifyModified();
        }
        if (!Utils.HasPlugin(NavmeshIPC.Name))
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Loc.Format("This feature requires {0} to be installed.", "此功能需要安装 {0}。", NavmeshIPC.Name));

        if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus))
        {
            if (wp.InteractWithOID == default)
            {
                var target = Service.TargetManager.Target;
                if (target != null)
                {
                    wp.Position = target.Position;
                    wp.Radius = RouteDB.DefaultInteractionRadius;
                    wp.InteractWithName = target.Name.ToString().ToLower();
                    wp.InteractWithOID = target.BaseId;
                    RouteDB.NotifyModified();
                }
            }
            else
                wp.InteractWithOID = default;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Tr("Add/Remove target from waypoint", "为路点添加/移除目标"));
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.CommentDots))
        {
            wp.showInteractions ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Tr("Toggle Interactions", "显示/隐藏交互设置"));
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.Clock))
        {
            wp.showWaits ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.Tr("Toggle Waits", "显示/隐藏等待设置"));

        if (wp.showInteractions)
        {
            if (UICombo.Enum(Loc.Tr("Interaction Type", "交互类型"), ref wp.Interaction))
                RouteDB.NotifyModified();
            switch (wp.Interaction)
            {
                case InteractionType.None: break;
                case InteractionType.Standard: break;
                case InteractionType.StartRoute:
                    if (UICombo.String(Loc.Tr("Route Name", "路线名称"), RouteDB.Routes.Select(r => r.Name).ToArray(), ref wp.RouteName))
                        RouteDB.NotifyModified();
                    break;
                case InteractionType.NodeScan:
                    ImGui.SameLine();
                    Utils.WorkInProgressIcon();
                    ImGuiComponents.HelpMarker(Loc.Tr("Node scanning will check the object table for nearby targetable gathering points, failing that will use your gatherer's reveal node ability and navigate to that. It will create a new phantom waypoint with the aforementioned information and navigate to it. Every phantom waypoint will also node scan. These special waypoints do not get saved to the route.", "节点扫描会先检查对象列表中附近可选中的采集点；如果没有，就会使用采集职业的显现技能并导航过去。它会根据这些信息创建临时幻影路点并前往目标。每个幻影路点也会继续执行节点扫描。这些特殊路点不会保存到路线中。"));
                    ImGui.TextUnformatted(Loc.Tr("This feature will have trouble with land nodes at the moment.", "这个功能目前在地面采集点上可能还不太稳定。"));
                    break;
            }
        }

        if (wp.showWaits)
        {
            if (ImGui.InputFloat2(Loc.Tr("Eorzean Time Wait", "艾欧泽亚时间等待"), ref wp.WaitTimeET))
                RouteDB.NotifyModified();
            if (ImGui.SliderInt(Loc.Tr("Wait (ms)", "等待（毫秒）"), ref wp.WaitTimeMs, 0, 60000))
                RouteDB.NotifyModified();
            if (UICombo.Enum(Loc.Tr("Wait for Condition", "等待条件"), ref wp.WaitForCondition))
                RouteDB.NotifyModified();
        }
    }

    private void ContextMenuGroup(string group)
    {
        var old = group;
        ImGuiEx.TextV(Loc.Tr("Name: ", "名称："));
        ImGui.SameLine();
        if (ImGui.InputText("##groupname", ref group, 256))
        {
            RouteDB.Routes.Where(r => r.Group == old).ToList().ForEach(r => r.Group = group);
            RouteDB.NotifyModified();
        }
    }

    private void ContextMenuRoute(Route r)
    {
        var group = r.Group;
        ImGuiEx.TextV(Loc.Tr("Group: ", "分组："));
        ImGui.SameLine();
        if (ImGui.InputText("##group", ref group, 256))
        {
            r.Group = group;
            RouteDB.NotifyModified();
        }
        if (ImGui.BeginMenu(Loc.Tr("Add Route to Existing Group", "将路线加入现有分组")))
        {
            var groupsCmr = GetGroups(RouteDB, true);
            foreach (var groupCmr in groupsCmr)
            {
                if (ImGui.MenuItem(DisplayGroup(groupCmr)))
                    r.Group = groupCmr;
                RouteDB.NotifyModified();
            }
            ImGui.EndMenu();
        }
    }

    private static string DisplayGroup(string group)
        => group == "Ungrouped" ? Loc.Tr("Ungrouped", "未分组") : group;

    private void ContextMenuWaypoint(Route r, int i)
    {
        if (ImGui.MenuItem(Loc.Tr("Execute this step only", "仅执行此步骤")))
            Exec.Start(r, i, false, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem(Loc.Tr("Execute route once starting from this step", "从此步骤开始执行一次路线")))
            Exec.Start(r, i, true, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem(Loc.Tr("Execute route starting from this step and then loop", "从此步骤开始执行并循环路线")))
            Exec.Start(r, i, true, true, r.Waypoints[i].Pathfind);

        var movementType = Service.Condition[ConditionFlag.InFlight] ? Movement.MountFly : Service.Condition[ConditionFlag.Mounted] ? Movement.MountNoFly : Movement.Normal;
        var target = Service.TargetManager.Target;

        if (ImGui.MenuItem(r.Waypoints[i].InteractWithOID != default ? Loc.Tr("Swap to normal waypoint", "切换为普通路点") : Loc.Tr("Swap to interact waypoint", "切换为交互路点")))
        {
            _postDraw.Add(() =>
            {
                r.Waypoints[i].InteractWithOID = r.Waypoints[i].InteractWithOID != default ? default : target?.BaseId ?? default;
                RouteDB.NotifyModified();
            });
        }

        if (ImGui.MenuItem(Loc.Tr("Insert step above", "在上方插入步骤")))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (Service.ObjectTable.LocalPlayer != null)
                    {
                        r.Waypoints.Insert(i, new() { Position = Player.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (target != null)
                    {
                        r.Waypoints.Insert(i, new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType, InteractWithOID = target.BaseId, InteractWithName = target.Name.ToString().ToLower() });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }

        if (ImGui.MenuItem(Loc.Tr("Insert step below", "在下方插入步骤")))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (Service.ObjectTable.LocalPlayer != null)
                    {
                        r.Waypoints.Insert(i + 1, new() { Position = Player.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (target != null)
                    {
                        r.Waypoints.Insert(i + 1, new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType, InteractWithOID = target.BaseId, InteractWithName = target.Name.ToString().ToLower() });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }

        if (ImGui.MenuItem(Loc.Tr("Move up", "上移")))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i - 1, wp);
                    RouteDB.NotifyModified();
                }
            });
        }

        if (ImGui.MenuItem(Loc.Tr("Move down", "下移")))
        {
            _postDraw.Add(() =>
            {
                if (i + 1 < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i + 1, wp);
                    RouteDB.NotifyModified();
                }
            });
        }

        if (ImGui.MenuItem(Loc.Tr("Delete", "删除")))
        {
            _postDraw.Add(() =>
            {
                if (i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    r.Waypoints.RemoveAt(i);
                    RouteDB.NotifyModified();
                }
            });
        }
    }
}
