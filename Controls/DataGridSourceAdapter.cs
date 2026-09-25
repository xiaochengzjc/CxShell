using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using AtomUI.Desktop.Controls;
using Avalonia.Data;

namespace CxShell.Controls;

/// <summary>
/// Creates the range-based local source required by AtomUI DataGrid 6.2 and
/// keeps the source's row keys mapped to the application's item instances.
/// This is deliberately a data-source helper, not a replacement DataGrid.
/// </summary>
public sealed class DataGridSourceAdapter<T> : IDisposable
{
    private readonly IReadOnlyList<T> _items;
    private readonly Dictionary<DataGridRowKey, T> _itemsByKey = new();
    private readonly DataGridLocalSource<T> _source;
    private bool _disposed;

    public DataGridSourceAdapter(IReadOnlyList<T> items, bool snapshot = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = snapshot ? items.ToArray() : items;

        var descriptor = DataGridLocalSourceDescriptor.For<T>(GetRowKey);
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetMethod == null || property.GetIndexParameters().Length != 0)
                continue;

            var addField = typeof(DataGridSourceAdapter<T>)
                .GetMethod(nameof(AddReflectedField), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(property.PropertyType);
            addField.Invoke(null, [descriptor, property]);
        }

        _source = DataGridLocalSource<T>.Create(
            _items,
            descriptor,
            new DataGridLocalSourceOptions());
    }

    public IDataGridSource Source => _source;

    public bool TryGetItem(DataGridRowKey key, out T item)
    {
        RebuildKeyMap();
        return _itemsByKey.TryGetValue(key, out item!);
    }

    public DataGridRowKey GetKey(T item)
    {
        RebuildKeyMap();
        return GetKeyForItem(item);
    }

    public IReadOnlyList<T> GetSelectedItems(DataGrid grid)
    {
        RebuildKeyMap();
        return grid.Selection.ExplicitKeys
            .Where(key => _itemsByKey.ContainsKey(key))
            .Select(key => _itemsByKey[key])
            .ToList();
    }

    public void SetSelectedItems(DataGrid grid, IEnumerable<T> items)
    {
        RebuildKeyMap();
        var selectedKeys = items
            .Select(GetKeyForItem)
            .Where(key => key.IsValid)
            .Distinct()
            .ToImmutableArray();

        grid.Selection = selectedKeys.IsDefaultOrEmpty
            ? DataGridSelectionState.Empty
            : new DataGridSelectionState(
                selectedKeys,
                null,
                ImmutableArray<DataGridSelectionInterval>.Empty,
                ImmutableArray<DataGridRowKey>.Empty);
        grid.CurrentRowKey = selectedKeys.IsDefaultOrEmpty ? null : selectedKeys[0];
    }

    public T? GetCurrentItem(DataGrid grid)
    {
        RebuildKeyMap();
        return grid.CurrentRowKey is { } key && _itemsByKey.TryGetValue(key, out var item)
            ? item
            : grid.Selection.ExplicitKeys.FirstOrDefault() is { } selectedKey &&
              _itemsByKey.TryGetValue(selectedKey, out item)
                ? item
                : default;
    }

    public void ConfigureColumns(DataGrid grid)
    {
        var fields = _source.Schema.Fields.Select(field => field.Id).ToHashSet();
        foreach (var column in grid.Columns)
        {
            if (column.FieldId is { IsValid: true } ||
                column is not DataGridBoundColumn boundColumn ||
                TryGetBindingPath(boundColumn.Binding) is not { } path)
                continue;

            var fieldId = new DataGridFieldId(path);
            if (fields.Contains(fieldId))
                column.FieldId = fieldId;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _source.Dispose();
    }

    private DataGridRowKey GetRowKey(T item)
    {
        var key = DataGridRowKey.FromString(GetStableKeyText(item));
        _itemsByKey[key] = item;
        return key;
    }

    private DataGridRowKey GetKeyForItem(T item)
    {
        var key = DataGridRowKey.FromString(GetStableKeyText(item));
        return _itemsByKey.ContainsKey(key) && ReferenceEquals(_itemsByKey[key], item)
            ? key
            : _itemsByKey.FirstOrDefault(pair => ReferenceEquals(pair.Value, item)).Key;
    }

    private void RebuildKeyMap()
    {
        _itemsByKey.Clear();
        foreach (var item in _items)
            GetRowKey(item);
    }

    private static void AddReflectedField<TValue>(
        DataGridLocalSourceDescriptor<T> descriptor,
        PropertyInfo property)
    {
        var fieldId = new DataGridFieldId(property.Name);
        Func<T, TValue> getter = item =>
        {
            var value = property.GetValue(item);
            return value is null ? default! : (TValue)value;
        };

        descriptor.Field(
            fieldId,
            getter,
            CreateComparer<TValue>(),
            static value => ToScalar(value),
            sortDirections: SupportsScalar(property.PropertyType)
                ? DataGridSortDirections.All
                : DataGridSortDirections.None);
    }

    private static IComparer<TValue> CreateComparer<TValue>()
    {
        if (typeof(IComparable).IsAssignableFrom(typeof(TValue)) ||
            typeof(IComparable<TValue>).IsAssignableFrom(typeof(TValue)))
            return Comparer<TValue>.Default;

        return Comparer<TValue>.Create((left, right) =>
            StringComparer.Ordinal.Compare(left?.ToString(), right?.ToString()));
    }

    private static DataGridScalar ToScalar<TValue>(TValue value)
    {
        object? boxed = value;
        return boxed switch
        {
            null => DataGridScalar.Null,
            bool item => DataGridScalar.FromBoolean(item),
            sbyte item => DataGridScalar.FromInt64(item),
            short item => DataGridScalar.FromInt64(item),
            int item => DataGridScalar.FromInt64(item),
            long item => DataGridScalar.FromInt64(item),
            byte item => DataGridScalar.FromUInt64(item),
            ushort item => DataGridScalar.FromUInt64(item),
            uint item => DataGridScalar.FromUInt64(item),
            ulong item => DataGridScalar.FromUInt64(item),
            float item => DataGridScalar.FromDouble(item),
            double item => DataGridScalar.FromDouble(item),
            decimal item => DataGridScalar.FromDecimal(item),
            string item => DataGridScalar.FromString(item),
            Guid item => DataGridScalar.FromGuid(item),
            DateOnly item => DataGridScalar.FromDateOnly(item),
            TimeOnly item => DataGridScalar.FromTimeOnly(item),
            DateTimeOffset item => DataGridScalar.FromDateTimeOffset(item),
            _ => DataGridScalar.FromString(boxed.ToString() ?? string.Empty)
        };
    }

    private static bool SupportsScalar(Type type)
    {
        var nonNullable = Nullable.GetUnderlyingType(type) ?? type;
        return nonNullable.IsEnum ||
               nonNullable == typeof(bool) ||
               nonNullable == typeof(string) ||
               nonNullable == typeof(Guid) ||
               nonNullable == typeof(DateOnly) ||
               nonNullable == typeof(TimeOnly) ||
               nonNullable == typeof(DateTimeOffset) ||
               nonNullable == typeof(decimal) ||
               nonNullable == typeof(double) ||
               nonNullable == typeof(float) ||
               nonNullable == typeof(byte) ||
               nonNullable == typeof(sbyte) ||
               nonNullable == typeof(short) ||
               nonNullable == typeof(ushort) ||
               nonNullable == typeof(int) ||
               nonNullable == typeof(uint) ||
               nonNullable == typeof(long) ||
               nonNullable == typeof(ulong);
    }

    private static string? TryGetBindingPath(BindingBase? binding) => binding switch
    {
        ReflectionBinding reflectionBinding => reflectionBinding.Path,
        CompiledBinding compiledBinding => compiledBinding.Path?.ToString(),
        _ => null
    };

    private static string GetStableKeyText(T item)
    {
        if (item == null)
            return "null";

        var type = item.GetType();
        foreach (var nestedPropertyName in new[] { "Session", "Entry", "Recording" })
        {
            var nested = type.GetProperty(nestedPropertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
            var nestedId = nested?.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?.GetValue(nested);
            if (nestedId != null)
                return $"{type.FullName}:{nestedPropertyName}:{nestedId}:{RuntimeHelpers.GetHashCode(item)}";
        }

        foreach (var propertyName in new[] { "Id", "Key", "FullPath", "Path", "Name" })
        {
            var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            var value = property?.GetValue(item);
            if (value != null && !string.IsNullOrWhiteSpace(value.ToString()))
                return $"{type.FullName}:{propertyName}:{value}:{RuntimeHelpers.GetHashCode(item)}";
        }

        return $"{type.FullName}:{RuntimeHelpers.GetHashCode(item)}";
    }
}
