using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System.Globalization;
using System.Linq;
using System.Numerics;
using visland.Export;
using visland.Farm;
using visland.Gathering;
using visland.Gathering.AutoGather;
using visland.Granary;
using visland.Helpers;
using visland.Pasture;
using visland.Workshop;

namespace visland;

public sealed class Plugin : IDalamudPlugin {
    public static string Name => "Visland-CN";
    public static string InternalName => "visland_cn";
    public static string CommandName => "vislandcn";
    public static string Repo => "https://puni.sh/api/repository/veyn";
    internal static string HelpMessage => Loc.Tr(
        "Opens the Gathering Menu\n" +
        $"/{CommandName} moveto <X> <Y> <Z> → move to raw coordinates\n" +
        $"/{CommandName} movedir <X> <Y> <Z> → move this many units over (relative to player facing)\n" +
        $"/{CommandName} stop → stop current route\n" +
        $"/{CommandName} pause → pause current route\n" +
        $"/{CommandName} resume → resume current route\n" +
        $"/{CommandName} exec <name> → run route by name continuously\n" +
        $"/{CommandName} execonce <name> → run route by name once\n" +
        $"/{CommandName} exectemp <base64 route> → run unsaved route continuously\n" +
        $"/{CommandName} exectemponce <base64 route> → run unsaved route once",
        "打开采集界面\n" +
        $"/{CommandName} moveto <X> <Y> <Z> → 移动到绝对坐标\n" +
        $"/{CommandName} movedir <X> <Y> <Z> → 按当前朝向相对移动指定距离\n" +
        $"/{CommandName} stop → 停止当前路线\n" +
        $"/{CommandName} pause → 暂停当前路线\n" +
        $"/{CommandName} resume → 恢复当前路线\n" +
        $"/{CommandName} exec <name> → 循环执行指定名称的路线\n" +
        $"/{CommandName} execonce <name> → 执行指定名称的路线一次\n" +
        $"/{CommandName} exectemp <base64 route> → 循环执行未保存的临时路线\n" +
        $"/{CommandName} exectemponce <base64 route> → 执行未保存的临时路线一次");

    internal static Plugin P = null!;

    private readonly AutoGatherController _autoGather;
    private readonly WindowSystem _windowSystem = new(InternalName);

    public unsafe Plugin(IDalamudPluginInterface dalamud) {
        var dir = dalamud.ConfigDirectory;
        if (!dir.Exists)
            dir.Create();

        Service.Init(dalamud);

        P = this;
        _windowSystem.Add(new GatherWindow(), new WorkshopWindow(), new GranaryWindow(), new PastureWindow(), new FarmWindow(), new ExportWindow());
        _autoGather = new AutoGatherController();

        Service.Interface.UiBuilder.Draw += OnDraw;
        Service.CommandManager.AddHandler($"/{CommandName}", new CommandInfo(OnCommand) { HelpMessage = HelpMessage });
        Service.Interface.UiBuilder.OpenConfigUi += () => _windowSystem.Get<GatherWindow>()!.IsOpen = true;
    }

    public void Dispose() {
        Service.CommandManager.RemoveHandler($"/{CommandName}");
        Service.Interface.UiBuilder.Draw -= OnDraw;
        _autoGather.Dispose();
        _windowSystem.Dispose();
        Service.Dispose();
    }

    private void OnDraw() => _windowSystem.Draw();

    private void OnCommand(string command, string arguments) {
        Service.Log.Debug($"cmd: '{command}', args: '{arguments}'");
        if (arguments.Length == 0)
            _windowSystem.Get<GatherWindow>()!.IsOpen ^= true;
        else {
            var args = arguments.Split(' ');
            switch (args[0]) {
                case "moveto":
                    if (args.Length > 3)
                        MoveToCommand(args, false);
                    break;
                case "movedir":
                    if (args.Length > 3)
                        MoveToCommand(args, true);
                    break;
                case "stop":
                    Service.RouteExec.Finish();
                    break;
                case "pause":
                    Service.RouteExec.Paused = true;
                    break;
                case "resume":
                    Service.RouteExec.Paused = false;
                    break;
                case "exec":
                    ExecuteCommand(string.Join(" ", args.Skip(1)), false);
                    break;
                case "execonce":
                    ExecuteCommand(string.Join(" ", args.Skip(1)), true);
                    break;
                case "exectemp":
                    ExecuteTempRoute(args[1], false);
                    break;
                case "exectemponce":
                    ExecuteTempRoute(args[1], true);
                    break;
            }
        }
    }

    internal void ExecuteTempRoute(string base64, bool once) {
        (var _, var json) = Utils.FromCompressedBase64(base64);
        var route = Newtonsoft.Json.JsonConvert.DeserializeObject<GatherRouteDB.Route>(json);
        if (route != null)
            Service.RouteExec.Start(route, 0, true, !once);
        else
            Service.Log.Warning($"Failed to deserialize route from clipboard: {base64}");
    }

    internal void MoveToCommand(string[] args, bool relativeToPlayer) {
        var originActor = relativeToPlayer ? Service.ObjectTable.LocalPlayer : null;
        var origin = originActor?.Position ?? new();
        var offset = new Vector3(float.Parse(args[1], CultureInfo.InvariantCulture), float.Parse(args[2], CultureInfo.InvariantCulture), float.Parse(args[3], CultureInfo.InvariantCulture));
        var route = new GatherRouteDB.Route { Name = "Temporary", Waypoints = [] };
        route.Waypoints.Add(new() { Position = origin + offset, Radius = 0.5f, InteractWithName = "", InteractWithOID = 0 });
        Service.RouteExec.Start(route, 0, false, false);
    }

    internal void ExecuteCommand(string name, bool once) {
        var route = Service.RouteExec.RouteDB.Routes.Find(r => r.Name == name);
        if (route != null)
            Service.RouteExec.Start(route, 0, true, !once, route.Waypoints.ElementAt(0).Pathfind);
    }
}
