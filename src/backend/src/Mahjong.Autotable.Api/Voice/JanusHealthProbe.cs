using System.Text.Json;

namespace Mahjong.Autotable.Api.Voice;

/// <summary>
/// Phase K Wave 8 — Bishop. Health probe for the Janus Gateway HTTP
/// API used by the
/// <see cref="JanusSpectatorVoiceHub"/>. The probe issues a GET
/// against <c>{endpoint}/info</c> (the canonical Janus info route)
/// and reports the parsed <c>name</c> + <c>version</c> back to the
/// caller. Used at startup to fail fast when the
/// <see cref="VoiceOptions.SpectatorSfuImpl"/> flips to
/// <c>"Janus"</c> but the gateway is unreachable.
/// </summary>
public interface IJanusHealthProbe
{
    /// <summary>
    /// Returns a probe result describing the gateway's health. On
    /// success <see cref="JanusHealthResult.IsHealthy"/> = true and
    /// the <see cref="JanusHealthResult.Name"/> /
    /// <see cref="JanusHealthResult.Version"/> fields carry the
    /// reported identity. On failure <see cref="JanusHealthResult.Error"/>
    /// carries a short classifier and <see cref="JanusHealthResult.IsHealthy"/>
    /// = false.
    /// </summary>
    Task<JanusHealthResult> ProbeAsync(CancellationToken ct = default);
}

/// <summary>
/// Phase K Wave 8 — Bishop. Outcome of an
/// <see cref="IJanusHealthProbe.ProbeAsync"/> call.
/// </summary>
public sealed record JanusHealthResult(
    bool IsHealthy,
    string? Name,
    string? Version,
    string? Error);

/// <summary>
/// Phase K Wave 8 — Bishop. Default <see cref="IJanusHealthProbe"/>
/// implementation. Uses HttpClient to GET the
/// <c>{endpoint}/info</c> sub-route and parse the JSON envelope.
/// All exceptions (timeouts, DNS failures, 5xx responses) collapse
/// to the unhealthy state with a short classifier so the operator
/// log surfaces the cause without a stack trace.
/// </summary>
public sealed class JanusHealthProbe : IJanusHealthProbe, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly bool _ownsHttpClient;

    public JanusHealthProbe(string endpoint, HttpClient? http = null)
    {
        _endpoint = endpoint ?? string.Empty;
        if (http is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            _ownsHttpClient = true;
        }
        else
        {
            _http = http;
            _ownsHttpClient = false;
        }
    }

    public async Task<JanusHealthResult> ProbeAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
            return new JanusHealthResult(false, null, null, "endpoint-not-configured");

        var url = $"{_endpoint.TrimEnd('/')}/info";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
                return new JanusHealthResult(false, null, null, $"http-{(int)resp.StatusCode}");
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string? name = null, version = null;
            // Janus typically returns { janus: "server_info", data: { name, version_string, ... } }
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();
                if (data.TryGetProperty("version_string", out var v) && v.ValueKind == JsonValueKind.String) version = v.GetString();
            }
            if (root.TryGetProperty("name", out var topName) && topName.ValueKind == JsonValueKind.String) name ??= topName.GetString();
            if (root.TryGetProperty("version", out var topVer) && topVer.ValueKind == JsonValueKind.String) version ??= topVer.GetString();
            return new JanusHealthResult(true, name, version, null);
        }
        catch (TaskCanceledException)
        {
            return new JanusHealthResult(false, null, null, "timeout");
        }
        catch (HttpRequestException ex)
        {
            return new JanusHealthResult(false, null, null, $"http-error:{ex.GetType().Name}");
        }
        catch (JsonException)
        {
            return new JanusHealthResult(false, null, null, "non-json-body");
        }
        catch (Exception ex)
        {
            return new JanusHealthResult(false, null, null, $"error:{ex.GetType().Name}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _http.Dispose();
    }
}
