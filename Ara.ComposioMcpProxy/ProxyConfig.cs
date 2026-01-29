using System.Text.RegularExpressions;

public sealed class ProxyConfig
{
    public string UpstreamUrl { get; }
    public string ApiKeyHeaderName { get; }
    public string ApiKey { get; }

    public HashSet<string> AllowToolNames { get; }
    public HashSet<string> BlockToolNames { get; }
    public List<Regex> AllowToolRegex { get; }

    public ProxyConfig()
    {
        UpstreamUrl = GetRequired("COMPOSIO_MCP_URL");
        ApiKeyHeaderName = Environment.GetEnvironmentVariable("COMPOSIO_API_KEY_HEADER") ?? "x-api-key";
        ApiKey = GetRequired("COMPOSIO_API_KEY");

        AllowToolNames = ParseSet(Environment.GetEnvironmentVariable("MCP_ALLOW_TOOL_NAMES"));
        BlockToolNames = ParseSet(Environment.GetEnvironmentVariable("MCP_BLOCK_TOOL_NAMES") ??
                                  "ComposioBackend.TAVILY_SEARCH");

        // Optional: allow by regex (comma-separated)
        var rx = Environment.GetEnvironmentVariable("MCP_ALLOW_TOOL_REGEX");
        AllowToolRegex = new List<Regex>();
        if (!string.IsNullOrWhiteSpace(rx))
        {
            foreach (var part in rx.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                AllowToolRegex.Add(new Regex(part, RegexOptions.Compiled | RegexOptions.IgnoreCase));
        }
    }

    private static string GetRequired(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(v))
            throw new InvalidOperationException($"Missing required env var: {name}");
        return v.Trim();
    }

    private static HashSet<string> ParseSet(string? value)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value)) return set;
        foreach (var p in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            set.Add(p);
        return set;
    }

    public bool IsAllowedTool(string toolName)
    {
        if (BlockToolNames.Contains(toolName)) return false;

        // If allowlist is empty -> allow all (minus blocklist)
        var hasAllow = AllowToolNames.Count > 0 || AllowToolRegex.Count > 0;
        if (!hasAllow) return true;

        if (AllowToolNames.Contains(toolName)) return true;
        return AllowToolRegex.Any(r => r.IsMatch(toolName));
    }
}
