using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace Backrooms;

public sealed class BackroomsSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);
    public ToggleNode ShowOnSmallMap { get; set; } = new(true);
    public ToggleNode ShowOnLargeMap { get; set; } = new(true);
    public ToggleNode ShowMatchCount { get; set; } = new(false);
    public ToggleNode DebugMode { get; set; } = new(false);
    public RangeNode<int> Padding { get; set; } = new(4, 0, 16);
}
