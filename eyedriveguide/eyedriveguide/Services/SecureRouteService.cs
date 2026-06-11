// ============================================================
// SecureRouteService.cs — SECURITY FIX AS-3
// Replaces RouteService.cs
// Key change: API key passed as Authorization header,
//             NOT as a query-string parameter.
// Additional: validates API response before parsing.
// ============================================================
using EyeDriveGuide.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace EyeDriveGuide.Services;

public class SecureRouteService
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<SecureRouteService> _logger;

    // SECURITY FIX AS-3: max response size to prevent memory exhaustion
    private const int MaxResponseBytes = 5 * 1024 * 1024; // 5 MB

    public SecureRouteService(
        HttpClient http,
        IMemoryCache cache,
        IConfiguration config,
        ILogger<SecureRouteService> logger)
    {
        _http = http;
        _cache = cache;
        _config = config;
        _logger = logger;
    }

    public async Task<RouteGraph> LoadRouteAsync(
        double startLat, double startLng,
        double endLat, double endLng)
    {
        var cacheKey = $"route:{startLat:F4},{startLng:F4}->{endLat:F4},{endLng:F4}";
        if (_cache.TryGetValue(cacheKey, out RouteGraph? cached) && cached != null)
            return cached;

        // SECURITY FIX AS-3: read key from environment / user-secrets, never appsettings.json
        var apiKey = _config["OpenRouteService__ApiKey"]
                  ?? Environment.GetEnvironmentVariable("OpenRouteService__ApiKey");

        RouteGraph graph;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            graph = BuildDemoRoute(startLat, startLng, endLat, endLng);
        }
        else
        {
            try
            {
                graph = await FetchFromOpenRouteServiceAsync(
                    startLat, startLng, endLat, endLng, apiKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "ORS API call failed, falling back to demo route");
                graph = BuildDemoRoute(startLat, startLng, endLat, endLng);
            }
        }

        _cache.Set(cacheKey, graph, TimeSpan.FromMinutes(30));
        return graph;
    }

    private async Task<RouteGraph> FetchFromOpenRouteServiceAsync(
        double startLat, double startLng,
        double endLat, double endLng,
        string apiKey)
    {
        // SECURITY FIX AS-3: API key in Authorization header, NOT query string
        // This keeps the key out of server logs, browser history, and referrer headers
        var url = "https://api.openrouteservice.org/v2/directions/driving-car" +
                  $"?start={startLng},{startLat}&end={endLng},{endLat}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Headers.Add("Accept", "application/json");

        using var response = await _http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead);

        // SECURITY FIX: check status before reading body
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("ORS API returned {StatusCode}", response.StatusCode);
            return BuildDemoRoute(startLat, startLng, endLat, endLng);
        }

        // SECURITY FIX: limit response size to prevent memory exhaustion
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > MaxResponseBytes)
            throw new InvalidOperationException("ORS response too large");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var limitedStream = new LimitedStream(stream, MaxResponseBytes);
        using var doc = await JsonDocument.ParseAsync(limitedStream);

        return ParseOrsResponse(doc, startLat, startLng, endLat, endLng);
    }

    private static RouteGraph ParseOrsResponse(
        JsonDocument doc,
        double startLat, double startLng,
        double endLat, double endLng)
    {
        var graph = new RouteGraph
        {
            StartCoord = new GeoCoordinate { Lat = startLat, Lng = startLng },
            EndCoord = new GeoCoordinate { Lat = endLat, Lng = endLng },
            IsLoaded = true
        };

        if (!doc.RootElement.TryGetProperty("features", out var features) ||
            features.GetArrayLength() == 0)
        {
            graph.ErrorMessage = "No route found";
            return graph;
        }

        var route = features[0];

        if (!route.TryGetProperty("properties", out var props) ||
            !props.TryGetProperty("summary", out var summary))
        {
            graph.ErrorMessage = "Unexpected ORS response format";
            return graph;
        }

        graph.TotalDistanceMetres = summary.GetProperty("distance").GetDouble();

        // ... (coordinate parsing same as original FetchFromOpenRouteServiceAsync) ...
        return graph;
    }

    // ... (BuildDemoRoute, InferSpeedLimit, etc. unchanged from original) ...

    private static RouteGraph BuildDemoRoute(
        double startLat, double startLng,
        double endLat, double endLng)
    {
        // Identical to original BuildDemoRoute — omitted for brevity
        return new RouteGraph
        {
            StartCoord = new GeoCoordinate { Lat = startLat, Lng = startLng },
            EndCoord = new GeoCoordinate { Lat = endLat, Lng = endLng },
            IsLoaded = true,
            ErrorMessage = "Demo mode — configure OpenRouteService__ApiKey env var for real routing"
        };
    }
}

/// <summary>
/// Stream wrapper that throws if more than maxBytes are read.
/// Prevents memory exhaustion from oversized API responses.
/// </summary>
internal class LimitedStream(Stream inner, long maxBytes) : Stream
{
    private long _bytesRead = 0;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var n = inner.Read(buffer, offset, count);
        _bytesRead += n;
        if (_bytesRead > maxBytes)
            throw new InvalidOperationException($"Response exceeds {maxBytes} byte limit");
        return n;
    }

    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
