using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

public sealed class McpProxyFunction
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly McpToolSanitizer _sanitizer;
    private readonly ProxyConfig _cfg;
    private readonly ILogger _log;

    public McpProxyFunction(
        IHttpClientFactory httpClientFactory,
        McpToolSanitizer sanitizer,
        ProxyConfig cfg,
        ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _sanitizer = sanitizer;
        _cfg = cfg;
        _log = loggerFactory.CreateLogger<McpProxyFunction>();
    }

    [Function("McpProxy")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", "post", "put", "patch", "delete", "options", Route = "mcp/{*rest}")] HttpRequestData req,
        string? rest)
    {
        // CORS preflight (optional)
        if (req.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            var pre = req.CreateResponse(HttpStatusCode.NoContent);
            AddCors(pre);
            return pre;
        }

        var upstream = BuildUpstreamUri(req, rest);

        using var upstreamReq = new HttpRequestMessage(new HttpMethod(req.Method), upstream);

        // Copy body (if any)
        if (req.Body != null && req.Method is not "GET" && req.Method is not "HEAD")
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            ms.Position = 0;
            upstreamReq.Content = new ByteArrayContent(ms.ToArray());

            // Preserve content-type if present
            if (req.Headers.TryGetValues("Content-Type", out var ctv))
                upstreamReq.Content.Headers.TryAddWithoutValidation("Content-Type", ctv.First());
        }

        // Copy headers except hop-by-hop and auth we control
        foreach (var h in req.Headers)
        {
            if (IsHopByHop(h.Key)) continue;
            if (string.Equals(h.Key, _cfg.ApiKeyHeaderName, StringComparison.OrdinalIgnoreCase)) continue;

            upstreamReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        // Inject composio API key
        upstreamReq.Headers.TryAddWithoutValidation(_cfg.ApiKeyHeaderName, _cfg.ApiKey);

        var client = _httpClientFactory.CreateClient("mcp");

        HttpResponseMessage upstreamResp;
        try
        {
            upstreamResp = await client.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Upstream call failed");
            var err = req.CreateResponse(HttpStatusCode.BadGateway);
            AddCors(err);
            await err.WriteStringAsync("Bad gateway calling MCP upstream.");
            return err;
        }

        // Build response
        var resp = req.CreateResponse(upstreamResp.StatusCode);
        AddCors(resp);

        // Copy headers
        foreach (var h in upstreamResp.Headers)
            resp.Headers.TryAddWithoutValidation(h.Key, h.Value);
        foreach (var h in upstreamResp.Content.Headers)
            resp.Headers.TryAddWithoutValidation(h.Key, h.Value);

        // If JSON response to tools/list, sanitize
        var contentType = upstreamResp.Content.Headers.ContentType?.MediaType ?? "";
        var bytes = await upstreamResp.Content.ReadAsByteArrayAsync();

        if (contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) && LooksLikeJsonRpcToolsListRequest(req))
        {
            try
            {
                var json = JsonNode.Parse(bytes);
                var sanitized = _sanitizer.SanitizeToolsListResponse(json, _cfg);

                if (sanitized is not null)
                {
                    var outBytes = Encoding.UTF8.GetBytes(sanitized.ToJsonString(new JsonSerializerOptions
                    {
                        WriteIndented = false
                    }));
                    resp.Body.Write(outBytes, 0, outBytes.Length);
                    return resp;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to sanitize tools/list response; passing through original.");
            }
        }

        // Default: passthrough
        resp.Body.Write(bytes, 0, bytes.Length);
        return resp;
    }

    private Uri BuildUpstreamUri(HttpRequestData req, string? rest)
    {
        // Upstream base includes query params like user_id already.
        // We append path after /api/mcp/{*rest} so you can call:
        //   https://<functionapp>/api/mcp  -> upstream base
        //   https://<functionapp>/api/mcp/sse -> upstream base with /sse appended (if your upstream supports it)
        var baseUri = new Uri(_cfg.UpstreamUrl);

        // If caller provides additional path segments, append them.
        // Keep existing query from upstream base AND caller query (caller wins if duplicates).
        var extraPath = string.IsNullOrWhiteSpace(rest) ? "" : (rest.StartsWith("/") ? rest : "/" + rest);

        var combinedPath = baseUri.AbsolutePath.TrimEnd('/') + extraPath;

        var qb = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // Upstream base query
        var baseQuery = System.Web.HttpUtility.ParseQueryString(baseUri.Query);
        foreach (var k in baseQuery.AllKeys)
            if (!string.IsNullOrWhiteSpace(k))
                qb[k!] = baseQuery[k!];

        // Caller query
        var callerQuery = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        foreach (var k in callerQuery.AllKeys)
            if (!string.IsNullOrWhiteSpace(k))
                qb[k!] = callerQuery[k!];

        var finalQuery = string.Join("&", qb.Select(kvp =>
            $"{System.Web.HttpUtility.UrlEncode(kvp.Key)}={System.Web.HttpUtility.UrlEncode(kvp.Value ?? "")}"));

        var builder = new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port, combinedPath, finalQuery);
        return builder.Uri;
    }

    private static bool IsHopByHop(string headerName)
    {
        return headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("TE", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeJsonRpcToolsListRequest(HttpRequestData req)
    {
        // Heuristic: only sanitize when request body includes "tools/list".
        // Read request body is non-trivial here without buffering; we instead check a header that Foundry sends,
        // but that's not guaranteed. So we sanitize responses always IF response is JSON and route is /mcp.
        // If you want stricter matching, add a query flag like ?sanitize=1 and check it here.
        return req.Url.AbsolutePath.Contains("/api/mcp", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddCors(HttpResponseData resp)
    {
        // Adjust for your domains if needed.
        resp.Headers.TryAddWithoutValidation("Access-Control-Allow-Origin", "*");
        resp.Headers.TryAddWithoutValidation("Access-Control-Allow-Methods", "GET,POST,PUT,PATCH,DELETE,OPTIONS");
        resp.Headers.TryAddWithoutValidation("Access-Control-Allow-Headers", "*");
    }
}
