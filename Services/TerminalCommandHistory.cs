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
    private readonly List<int> _navigationMatches = new();
    private int _navigationMatchPosition = -1;
    private string _draft = string.Empty;

    public TerminalCommandHistory(int capacity = DefaultCapacity)
    {
        _capacity = Math.Max(1, capacity);
    }

    public int Count => _entries.Count;

    public IReadOnlyList<string> Entries => _entries;

    public bool IsNavigating => _navigationMatches.Count > 0;

    public void Add(string? command)
    {
        var normalized = command?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            ResetNavigation();
            return;
        }

        if (_entries.Count == 0 || !string.Equals(_entries[^1], normalized, StringComparison.Ordinal))
            _entries.Add(normalized);

        while (_entries.Count > _capacity)
            _entries.RemoveAt(0);

        ResetNavigation();
    }

    public string? MovePrevious(string currentLine)
    {
        if (_entries.Count == 0)
            return null;

        if (_navigationMatches.Count == 0)
        {
            _draft = currentLine ?? string.Empty;
            var prefix = _draft.TrimStart();
            for (var index = 0; index < _entries.Count; index++)
            {
                if (prefix.Length == 0 ||
                    _entries[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _navigationMatches.Add(index);
                }
            }

            if (_navigationMatches.Count == 0)
                return null;

            _navigationMatchPosition = _navigationMatches.Count - 1;
        }
        else if (_navigationMatchPosition > 0)
        {
            _navigationMatchPosition--;
        }

        return _entries[_navigationMatches[_navigationMatchPosition]];
    }

    public string? MoveNext()
    {
        if (_navigationMatches.Count == 0)
            return null;

        if (_navigationMatchPosition < _navigationMatches.Count - 1)
        {
            _navigationMatchPosition++;
            return _entries[_navigationMatches[_navigationMatchPosition]];
        }

        var draft = _draft;
        ResetNavigation();
        return draft;
    }

    public void ResetNavigation()
    {
        _navigationMatches.Clear();
        _navigationMatchPosition = -1;
        _draft = string.Empty;
    }

    public void Clear()
    {
        _entries.Clear();
        ResetNavigation();
    }
}
