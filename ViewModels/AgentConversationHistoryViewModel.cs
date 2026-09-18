using CxShell.Services;
using CxShell.Services.Agent;

namespace CxShell.ViewModels;

public sealed class AgentConversationHistoryViewModel
{
    private readonly AgentConversationHistoryRecord _record;

    public AgentConversationHistoryViewModel(AgentConversationHistoryRecord record)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
    }

    public string ConversationId => _record.ConversationId;
    public AgentConversationHistoryRecord Record => _record;
    public string Title => _record.Title;
    public string Preview
        => _record.Messages?
               .LastOrDefault(message => string.Equals(message.Kind, "user", StringComparison.OrdinalIgnoreCase))
               ?.Content
           ?? Text("Agent.HistoryEmpty");
    public string TimeText => _record.UpdatedAtUtc.ToLocalTime().ToString("g");
    public string TargetText => string.IsNullOrWhiteSpace(_record.SessionLabel)
        ? Text("Agent.NoSession")
        : _record.SessionLabel!;
    public string ModeText => _record.Mode switch
    {
        AgentChatMode.Chat => Text("Agent.ModeChat"),
        AgentChatMode.Plan => Text("Agent.ModePlan"),
        _ => Text("Agent.ModeAgent")
    };
    public string MessageCountText
        => string.Format(Text("Agent.HistoryMessageCount"), _record.Messages?.Count ?? 0);
    public string MetadataText => $"{ModeText} · {TargetText} · {MessageCountText}";

    public bool MatchesSearch(string search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return true;

        return Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               Preview.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               TargetText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               ModeText.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (_record.Model?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}
