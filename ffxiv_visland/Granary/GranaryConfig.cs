using visland.Helpers;

namespace visland.Granary;

public class GranaryConfig : Configuration.Node
{
    public enum UpdateStrategy
    {
        [LocalizedDescription("Manual", "手动")]
        Manual,

        [LocalizedDescription("Max out, keep same destination", "派满并保持当前目的地")]
        MaxCurrent,

        [LocalizedDescription("Max out and select expedition bringing rare resources with two lowest counts", "派满并选择带来两种库存最低稀有资源的探险")]
        BestDifferent,

        [LocalizedDescription("Max out and select expedition bringing rare resource with lowest count in both granaries", "派满并选择带来两个仓库合计库存最低稀有资源的探险")]
        BestSame,
    }

    public CollectStrategy Collect = CollectStrategy.Manual;
    public UpdateStrategy Reassign = UpdateStrategy.Manual;
}
