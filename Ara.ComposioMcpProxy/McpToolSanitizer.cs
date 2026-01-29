using System.Text.Json;
using System.Text.Json.Nodes;

public sealed class McpToolSanitizer
{
    // Foundry is strict. Keep schema conservative.
    // Strategy:
    // 1) Filter out known-bad tools (blocklist / allowlist).
    // 2) Validate inputSchema is a JSON object schema with simple properties.
    // 3) If invalid schema -> drop tool.
    //
    // You can switch to "rewrite" instead of "drop" later, but drop is safest.

    public JsonNode? SanitizeToolsListResponse(JsonNode? root, ProxyConfig cfg)
    {
        if (root is null) return null;

        // Expected JSON-RPC: { jsonrpc, id, result: { tools: [...] } }
        var result = root["result"];
        if (result is null) return root;

        var tools = result["tools"] as JsonArray;
        if (tools is null) return root;

        var sanitized = new JsonArray();

        foreach (var t in tools)
        {
            if (t is not JsonObject to) continue;

            var name = to["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!cfg.IsAllowedTool(name))
                continue;

            var inputSchema = to["inputSchema"];
            if (!IsValidInputSchema(inputSchema))
                continue;

            sanitized.Add(to);
        }

        ((JsonObject)result)["tools"] = sanitized;
        return root;
    }

    private static bool IsValidInputSchema(JsonNode? schema)
    {
        if (schema is null) return false;
        if (schema is not JsonObject obj) return false;

        // Must be object schema
        var type = obj["type"]?.GetValue<string>();
        if (!string.Equals(type, "object", StringComparison.OrdinalIgnoreCase))
            return false;

        // Reject complex constructs that Foundry often rejects
        if (obj.ContainsKey("oneOf") || obj.ContainsKey("anyOf") || obj.ContainsKey("allOf") || obj.ContainsKey("not"))
            return false;

        // properties (optional but if present must be object)
        if (obj.TryGetPropertyValue("properties", out var propsNode) && propsNode is not null && propsNode is not JsonObject)
            return false;

        // required (optional but if present must be array of strings)
        if (obj.TryGetPropertyValue("required", out var reqNode) && reqNode is not null)
        {
            if (reqNode is not JsonArray arr) return false;
            foreach (var item in arr)
            {
                if (item is null) return false;
                if (item is not JsonValue) return false;
                _ = item.GetValue<string>(); // throws if not string
            }
        }

        return true;
    }
}
