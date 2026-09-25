using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CxShell.Models;
using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class SessionRowOrderTests
{
    [Fact]
    public void ReorderSessionRows_Moves_Existing_Node_Without_Resetting_Grid()
    {
        var first = Session("first");
        var selected = Session("selected");
        var last = Session("last");
        var selectedNode = new SessionNodeViewModel(selected);
        var rows = new ObservableCollection<SessionNodeViewModel>
        {
            new(first), selectedNode, new(last)
        };
        var actions = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, args) => actions.Add(args.Action);

        SessionTreeViewModel.ReorderSessionRows(rows, [first, last, selected]);

        Assert.Equal([first.Id, last.Id, selected.Id], rows.Select(row => row.Session!.Id));
        Assert.Same(selectedNode, rows[2]);
        Assert.Equal([NotifyCollectionChangedAction.Move], actions);

        SessionTreeViewModel.ReorderSessionRows(rows, [first, last, selected]);
        Assert.Single(actions);
    }

    private static SessionInfo Session(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name
    };
}
