using visland.Helpers;

namespace visland;

public enum CollectStrategy
{
    [LocalizedDescription("Manual", "手动")]
    Manual,

    [LocalizedDescription("Automatic, if not overcapping", "自动，不允许溢出")]
    NoOvercap,

    [LocalizedDescription("Automatic, allow overcap", "自动，允许溢出")]
    FullAuto,
}
