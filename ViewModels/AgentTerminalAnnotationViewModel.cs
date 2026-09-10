using CommunityToolkit.Mvvm.ComponentModel;
using CxShell.Services;
using CxShell.Services.Agent;

namespace CxShell.ViewModels;

/// <summary>
/// A bounded reference to text selected in a visible terminal. The selected
/// text is sent as message context, while the UI only shows a compact marker.
/// </summary>
public sealed partial class AgentTerminalAnnotationViewModel : ObservableObject
{
    public const int MaximumTextCharacters = 16 * 1024;

    [ObservableProperty] private int _number;
    [ObservableProperty] private string _text;
    [ObservableProperty] private string _sourceLabel;

    public AgentTerminalAnnotationViewModel(int number, string text, string? sourceLabel = null)
    {
        Number = number;
        Text = text;
        SourceLabel = string.IsNullOrWhiteSpace(sourceLabel) ? string.Empty : sourceLabel.Trim();
    }

    public string DisplayText => $"{TextKey("Agent.Annotation")} {Number}";

    public string TooltipText
    {
        get
        {
            var source = string.IsNullOrWhiteSpace(SourceLabel)
                ? string.Empty
                : $"{TextKey("Agent.AnnotationSource")}: {SourceLabel}{Environment.NewLine}";
            return $"{source}{TextKey("Agent.AnnotationSelectedText")}{Environment.NewLine}{Text}";
        }
    }

    public string RemoveText => TextKey("Agent.RemoveAnnotation");

    public AgentContentPart ToContentPart()
        => AgentContentPart.TextPart(Text, $"terminal-annotation-{Number}.txt");

    public void NotifyLocalizationChanged()
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(TooltipText));
        OnPropertyChanged(nameof(RemoveText));
    }

    private static string TextKey(string key) => LocalizationService.Shared.Text(key);
}
