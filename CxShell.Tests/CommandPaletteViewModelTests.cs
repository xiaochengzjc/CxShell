using CxShell.ViewModels;

namespace CxShell.Tests;

public sealed class CommandPaletteViewModelTests
{
    [Fact]
    public void Open_FiltersByTitleHintAndTag()
    {
        var palette = new CommandPaletteViewModel(() =>
        [
            new CommandPaletteItem("Sessions", "Production", static () => { }, "admin@10.0.0.8:22", "SSH", true),
            new CommandPaletteItem("Commands", "Toggle monitor", static () => { }, tag: "Operations")
        ]);

        palette.Open();
        Assert.Equal(2, palette.ResultCount);

        palette.Query = "10.0.0.8";
        Assert.Single(palette.Groups);
        Assert.Single(palette.Groups[0].Items);
        Assert.Equal("Production", palette.SelectedItem?.Title);

        palette.Query = "operations";
        Assert.Equal("Toggle monitor", palette.SelectedItem?.Title);
    }

    [Fact]
    public void MoveAndExecute_ClosesPaletteAndInvokesSelectedItem()
    {
        var executed = string.Empty;
        var palette = new CommandPaletteViewModel(() =>
        [
            new CommandPaletteItem("Commands", "First", () => executed = "first"),
            new CommandPaletteItem("Commands", "Second", () => executed = "second")
        ]);

        palette.Open();
        palette.MoveDown();
        Assert.Equal("Second", palette.SelectedItem?.Title);

        palette.ExecuteSelected();

        Assert.False(palette.IsOpen);
        Assert.Equal("second", executed);
    }
}
