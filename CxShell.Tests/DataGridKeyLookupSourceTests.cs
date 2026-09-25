using System.Collections.ObjectModel;
using AtomUI.Desktop.Controls;
using CxShell.Controls;

namespace CxShell.Tests;

public sealed class DataGridKeyLookupSourceTests
{
    [Fact]
    public async Task LookupAsync_Finds_Uncached_Row_In_Sorted_Projection()
    {
        using var source = CreateSource(700);
        using var lookup = new DataGridKeyLookupSource(source);
        var query = DataGridQuery.Empty.WithSorts(
            [new(new DataGridFieldId(nameof(Row.Rank)), DataGridSortDirection.Descending)]);
        var snapshot = (await lookup.FetchAsync(FetchRequest(query), CancellationToken.None))
            .Snapshot;

        var result = await lookup.LookupAsync(
            new DataGridKeyLookupRequest(query, snapshot, DataGridRowKey.FromInt64(5)),
            CancellationToken.None);

        Assert.True(result.HasValue);
        Assert.Equal(694, result.Value.DisplayIndex);
        Assert.Equal(694, result.Value.DataIndex);
    }

    [Fact]
    public async Task LookupAsync_Returns_Null_For_Missing_Key()
    {
        using var source = CreateSource(140);
        using var lookup = new DataGridKeyLookupSource(source);
        var snapshot = (await lookup.FetchAsync(
            FetchRequest(DataGridQuery.Empty), CancellationToken.None)).Snapshot;

        var result = await lookup.LookupAsync(
            new DataGridKeyLookupRequest(
                DataGridQuery.Empty, snapshot, DataGridRowKey.FromInt64(9999)),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public void Move_Raises_Invalidation_From_Wrapper_Source()
    {
        var rows = new ObservableCollection<Row>(
            Enumerable.Range(0, 3).Select(index => new Row(index, index)));
        using var source = CreateSource(rows);
        using var lookup = new DataGridKeyLookupSource(source);
        object? invalidatedSender = null;
        lookup.Invalidated += (sender, _) => invalidatedSender = sender;

        rows.Move(0, 1);

        Assert.Same(lookup, invalidatedSender);
    }

    private static DataGridLocalSource<Row> CreateSource(int count)
    {
        var rows = Enumerable.Range(0, count).Select(index => new Row(index, index)).ToArray();
        return CreateSource(rows);
    }

    private static DataGridLocalSource<Row> CreateSource(IReadOnlyList<Row> rows)
    {
        var descriptor = DataGridLocalSourceDescriptor.For<Row>(
                row => DataGridRowKey.FromInt64(row.Id))
            .Field(
                new DataGridFieldId(nameof(Row.Rank)),
                row => row.Rank,
                Comparer<int>.Default,
                rank => DataGridScalar.FromInt64(rank));

        return DataGridLocalSource<Row>.Create(
            rows,
            descriptor,
            new DataGridLocalSourceOptions
            {
                PreferredRangeSize = 32,
                MaximumRangeSize = 64
            });
    }

    private static DataGridFetchRequest FetchRequest(DataGridQuery query) =>
        new(
            query,
            null,
            DataGridGroupExpansion.AllExpanded,
            new DataGridRange(0, 1),
            null,
            0,
            0);

    private sealed record Row(int Id, int Rank);
}
