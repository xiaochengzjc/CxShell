using System.Collections.Immutable;
using AtomUI.Desktop.Controls;
using CxShell.Controls;
using CxShell.Models;
using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class DataGridSourceAdapterTests
{
    [Fact]
    public void GetSelectedItems_MapsMultipleExplicitKeysToItems()
    {
        var items = CreateItems(3);
        using var adapter = new DataGridSourceAdapter<SessionRecordingItemViewModel>(items, snapshot: true);
        var grid = new DataGrid { SelectionMode = DataGridSelectionMode.Extended };
        var keys = items.Take(2).Select(adapter.GetKey).ToImmutableArray();
        grid.Selection = new DataGridSelectionState(
            keys,
            null,
            ImmutableArray<DataGridSelectionInterval>.Empty,
            ImmutableArray<DataGridRowKey>.Empty);

        var selected = adapter.GetSelectedItems(grid);

        Assert.Equal(items.Take(2), selected);
    }

    [Fact]
    public void GetSelectedItems_ExpandsIndexIntervalsForExtendedSelection()
    {
        var items = CreateItems(4);
        using var adapter = new DataGridSourceAdapter<SessionRecordingItemViewModel>(items, snapshot: true);
        var grid = new DataGrid { SelectionMode = DataGridSelectionMode.Extended };
        var scope = new DataGridSelectionScope(
            adapter.Source,
            DataGridQuery.Empty,
            new DataGridSnapshotId("recording-selection-test"));
        grid.Selection = new DataGridSelectionState(
            ImmutableArray<DataGridRowKey>.Empty,
            null,
            [new DataGridSelectionInterval(1, 3, scope)],
            ImmutableArray<DataGridRowKey>.Empty);

        var selected = adapter.GetSelectedItems(grid);

        Assert.Equal(items.Skip(1).Take(2), selected);
    }

    private static SessionRecordingItemViewModel[] CreateItems(int count)
    {
        return Enumerable.Range(0, count)
            .Select(index => new SessionRecordingItemViewModel(new SessionRecording
            {
                Id = Guid.NewGuid(),
                SessionLabel = $"session-{index}"
            }))
            .ToArray();
    }
}
