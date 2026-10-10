namespace DataGridScene.Core;

/// <summary>Expansion state keyed by stable group paths and record identities, not visual row indexes.</summary>
public sealed record GridViewState(IReadOnlyList<string> CollapsedGroups, IReadOnlyList<long> ExpandedDetails)
{
    public static GridViewState Empty { get; } = new(Array.Empty<string>(), Array.Empty<long>());
}
public sealed partial class GridView
{
    private HashSet<string> GroupKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<GridGroup>(Groups);
        while (pending.TryPop(out var group))
        {
            keys.Add(group.Key);
            foreach (var child in group.Children) pending.Push(child);
        }
        return keys;
    }
    public GridViewState CaptureState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var keys = GroupKeys();
        return new(Array.AsReadOnly(_collapsed.Where(keys.Contains).Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(_details.Where(Table.Contains).Order().ToArray()));
    }
    /// <summary>Unknown groups/records are ignored for schema/data evolution; one view change is raised.</summary>
    public void RestoreState(GridViewState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(state);
        if (state.CollapsedGroups is null || state.ExpandedDetails is null || state.CollapsedGroups.Count > 100000 || state.ExpandedDetails.Count > 1000000)
            throw new ArgumentException("Invalid or oversized expansion state.", nameof(state));
        if (state.CollapsedGroups.Any(key => string.IsNullOrEmpty(key) || key.Length > 8192))
            throw new ArgumentException("Invalid group path.", nameof(state));
        var keys = GroupKeys();
        var collapsed = state.CollapsedGroups.Where(keys.Contains).ToArray();
        var details = state.ExpandedDetails.Where(Table.Contains).ToArray();
        _collapsed.Clear(); _collapsed.UnionWith(collapsed);
        _details.Clear(); _details.UnionWith(details);
        Reflatten();
    }
}
