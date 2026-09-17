using System;
using System.Collections.Generic;

namespace CxShell.Services;

/// <summary>
/// Keeps a small, in-memory command history for one terminal session.
/// </summary>
public sealed class TerminalCommandHistory
{
    public const int DefaultCapacity = 100;

    private readonly int _capacity;
    private readonly List<string> _entries = new();

    public TerminalCommandHistory(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _entries.Count;

    public IReadOnlyList<string> Entries => _entries;

    public void Add(string? command)
    {
        var normalized = command?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (_entries.Count == 0 || !string.Equals(_entries[^1], normalized, StringComparison.Ordinal))
            _entries.Add(normalized);

        while (_entries.Count > _capacity)
            _entries.RemoveAt(0);

    }

    public void Clear()
    {
        _entries.Clear();
    }
}
