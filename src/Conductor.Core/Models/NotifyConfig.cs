namespace Conductor.Models;

public sealed class NotifyConfig
{
    /// <summary>Command run on needs-attention / completion. Placeholders in args: {message}</summary>
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = new();
    public WebhookNotifyConfig? Webhook { get; set; }
    public WebhookNotifyConfig? Discord { get; set; }
    public WebhookNotifyConfig? Slack { get; set; }
}

public sealed class WebhookNotifyConfig
{
    public string Url { get; set; } = "";
    public Dictionary<string, string>? Headers { get; set; }
}
