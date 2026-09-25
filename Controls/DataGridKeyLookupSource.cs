using AtomUI.Desktop.Controls;

namespace CxShell.Controls;

internal sealed class DataGridKeyLookupSource : IDataGridKeyLookupSource, IDisposable
{
    private readonly IDataGridSource _source;

    public DataGridKeyLookupSource(IDataGridSource source)
    {
        _source = source;
        _source.Invalidated += OnSourceInvalidated;
    }

    public DataGridSourceSchema Schema => _source.Schema;

    public event EventHandler? Invalidated;

    public ValueTask<DataGridRangeResult> FetchAsync(
        DataGridFetchRequest request,
        CancellationToken cancellationToken) =>
        _source.FetchAsync(request, cancellationToken);

    public void Dispose()
    {
        _source.Invalidated -= OnSourceInvalidated;
        Invalidated = null;
    }

    private void OnSourceInvalidated(object? sender, EventArgs args) =>
        Invalidated?.Invoke(this, EventArgs.Empty);

    public async ValueTask<DataGridKeyLookupResult?> LookupAsync(
        DataGridKeyLookupRequest request,
        CancellationToken cancellationToken)
    {
        // Key lookup does not carry group expansion state. The session grid
        // never groups rows, so do not return an incorrect grouped slot.
        if (!request.Query.Groups.IsEmpty)
            return null;

        var start = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _source.FetchAsync(
                new DataGridFetchRequest(
                    request.Query,
                    null,
                    DataGridGroupExpansion.AllExpanded,
                    new DataGridRange(start, Schema.MaximumRangeSize),
                    request.Snapshot,
                    0,
                    0),
                cancellationToken);

            for (var index = 0; index < result.Entries.Length; index++)
            {
                var entry = result.Entries[index];
                if (entry.Kind == DataGridSourceEntryKind.Data && entry.RowKey == request.RowKey)
                    return new DataGridKeyLookupResult(result.StartIndex + index, entry.DataIndex);
            }

            start += result.Entries.Length;
            if (result.Entries.IsEmpty || start >= result.TotalEntryCount)
                return null;
        }
    }
}
