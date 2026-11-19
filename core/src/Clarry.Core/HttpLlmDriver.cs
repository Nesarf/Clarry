using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Clarry.Core;

/// <summary>
/// Where an agent's brain lives. Credentials stay in process memory on purpose: the world
/// save file records what happened in the story, never a key.
/// </summary>
public sealed class HttpDriverOptions
{
    /// <summary>A chat-completions URL, or a base URL that /chat/completions is appended to.</summary>
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public double Temperature { get; set; } = 0.9;
    public int MaxTokens { get; set; } = 800;

    /// <summary>
    /// Generous on purpose. A timeout here reads to everyone downstream as "you failed", and
    /// failing an agent for thinking slowly is not a rule this theatre wants. Slow is not foul.
    /// </summary>
    public int TimeoutMs { get; set; } = 180_000;

    public string ResolvedUrl()
    {
        var url = (Endpoint ?? "").Trim();
        if (url.Length == 0) return url;
        if (url.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase)) return url;
        if (url.Contains("/completions", StringComparison.OrdinalIgnoreCase)) return url;
        return url.TrimEnd('/') + "/chat/completions";
    }
}

/// <summary>
/// Drives one character through any OpenAI-compatible chat endpoint. That covers a hosted
/// model, a local runner, and an agent that exposes its own HTTP face, which is the point:
/// Clarry does not care what is behind the URL.
/// </summary>
public sealed class HttpLlmDriver : IAgentDriver
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(15)
    });

    private readonly HttpDriverOptions _options;

    public HttpLlmDriver(HttpDriverOptions options) => _options = options;

    public string Kind => "http-llm";

    public async Task<DriverOutcome> DriveAsync(DriverContext ctx, CancellationToken ct)
    {
        var url = _options.ResolvedUrl();
        if (string.IsNullOrWhiteSpace(url)) return DriverOutcome.Fail("no endpoint configured");

        // Built once and measured, because this string is the bill. Every later decision about
        // trimming history or moving work into a faster language starts from this number.
        var system = DriverPrompt.System(ctx);
        var user = DriverPrompt.User(ctx);
        var promptChars = system.Length + user.Length;

        var payload = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(_options.Model) ? "default" : _options.Model,
            temperature = _options.Temperature,
            max_tokens = _options.MaxTokens,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Math.Clamp(_options.TimeoutMs, 1_000, 600_000));

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await Client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            watch.Stop();

            if (!response.IsSuccessStatusCode)
                return DriverOutcome.Fail($"http {(int)response.StatusCode}: {Truncate(body, 300)}",
                                          watch.ElapsedMilliseconds, promptChars);

            var text = ExtractAssistantText(body);
            if (text is null)
                return DriverOutcome.Fail("no assistant text in response", watch.ElapsedMilliseconds, promptChars);

            var (promptTokens, completionTokens, cachedTokens) = ExtractUsage(body);

            var drafts = OrderParser.ParseDrafts(text);
            if (drafts.Count == 0)
                return DriverOutcome.Fail("model returned no parsable order array: " + Truncate(text, 300),
                                          watch.ElapsedMilliseconds, promptChars);

            var outcome = new DriverOutcome
            {
                ElapsedMs = watch.ElapsedMilliseconds,
                PromptChars = promptChars,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                CachedTokens = cachedTokens
            };
            outcome.Orders.AddRange(OrderParser.ToOrders(ctx.Self.Id, drafts));
            if (outcome.Orders.Count == 0)
                outcome.Error = "order array had no recognised kind";
            return outcome;
        }
        catch (OperationCanceledException)
        {
            watch.Stop();
            return DriverOutcome.Fail(ct.IsCancellationRequested ? "cancelled" : "timed out",
                                      watch.ElapsedMilliseconds, promptChars);
        }
        catch (Exception e)
        {
            watch.Stop();
            return DriverOutcome.Fail(e.GetType().Name + ": " + e.Message,
                                      watch.ElapsedMilliseconds, promptChars);
        }
    }

    /// <summary>Reads the assistant text out of the shapes these endpoints actually return.</summary>
    private static string? ExtractAssistantText(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var content) &&
                        content.ValueKind == JsonValueKind.String)
                        return content.GetString();
                    if (choice.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                        return text.GetString();
                }
            }

            if (root.TryGetProperty("content", out var top) && top.ValueKind == JsonValueKind.String)
                return top.GetString();
            if (root.TryGetProperty("response", out var resp) && resp.ValueKind == JsonValueKind.String)
                return resp.GetString();
            if (root.ValueKind == JsonValueKind.String) return root.GetString();
        }
        catch (JsonException)
        {
            // Not JSON at all: some runners answer with the text directly.
            return body;
        }
        return null;
    }

    /// <summary>
    /// Reads usage out of the response. Field names differ between providers and some do not
    /// report at all, so every field here is optional: absent means "not told", never "zero".
    /// Reporting a made-up zero would be a lie that later optimisation gets built on.
    /// </summary>
    private static (int? Prompt, int? Completion, int? Cached) ExtractUsage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
                return (null, null, null);

            int? Read(string name) =>
                usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetInt32()
                    : null;

            return (Read("prompt_tokens"), Read("completion_tokens"), Read("prompt_cache_hit_tokens"));
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
