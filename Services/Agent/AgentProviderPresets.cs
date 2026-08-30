using CxShell.Models;

namespace CxShell.Services.Agent;

public static class AgentProviderPresets
{
    public static AgentProviderSettings CreateRoutinPlan()
    {
        return new AgentProviderSettings
        {
            Enabled = false,
            Name = "Routin AI（套餐）",
            BuiltinId = "routin-ai-plan",
            Type = AgentProviderType.OpenAiResponses,
            BaseUrl = "https://api.routin.ai/plan/v1",
            Model = "gpt-5.4",
            RequiresApiKey = true,
            RequestTimeoutSeconds = 300
        };
    }
}
