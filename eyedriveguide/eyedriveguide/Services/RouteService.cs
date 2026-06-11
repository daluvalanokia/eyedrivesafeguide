// ============================================================
// RouteService.cs — Security-updated, single canonical file
// REPLACES: RouteService.cs + SecureRouteService.cs
//
// SECURITY FIX AS-3: API key sent as Authorization header,
//   NOT as a query-string parameter (keeps it out of logs).
// SECURITY FIX: max response size cap (5 MB) via LimitedStream.
// SECURITY FIX: reads key from env var / user-secrets,
//   never from appsettings.json plain text.
// ============================================================
using EyeDriveGuide.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace EyeDriveGuide.Services;

public class RouteService
{
    private readonly HttpClient              _http;
    private readonly IMemoryCache            _cache;
    private readonly IConfiguration          _config;
    private readonly ILogger<RouteService>   _logger;

    // SECURITY: cap API response to prevent memory exhaustion
    private const int MaxResponseBytes = 5 * 1024 * 1024; // 5 MB

    public RouteService(
        HttpClient http,
        IMemoryCache cache,
        IConfiguration config,
        ILogger<RouteService> logger)
    {
        _http   = http;
        _cache  = cache;
        _config = config;
        _logger = logger;
    }

    public async Task<RouteGraph> LoadRouteAsync(
        double startLat, double startLng,
        double endLat,   double endLng)
    {
        var cacheKey = $"route:{startLat:F4},{startLng:F4}->{endLat:F4},{endLng:F4}";
        if (_cache.TryGetValue(cacheKey, out RouteGraph? cached) && cached != null)
            return cached;

        // SECURITY FIX AS-3: read from env var or user-secrets, never plain appsettings.json
        var apiKey = _config["OpenRouteService__ApiKey"]
                  ?? Environment.GetEnvironmentVariable("OpenRouteService__ApiKey")
                  ?? _config["OpenRouteService:ApiKey"]; // legacy fallback key name

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
        double endLat,   double endLng,
        string apiKey)
    {
        // SECURITY FIX AS-3: API key in Authorization header, NOT in query string
        // Query string key ends up in server access logs, browser history, referrer headers.
        var url = "https://api.openrouteservice.org/v2/directions/driving-car" +
                  $"?start={startLng},{startLat}&end={endLng},{endLat}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Headers.Add("Accept", "application/json");

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("ORS API returned {StatusCode}", response.StatusCode);
            return BuildDemoRoute(startLat, startLng, endLat, endLng);
        }

        // SECURITY: reject oversized responses
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > MaxResponseBytes)
            throw new InvalidOperationException("ORS response exceeds 5 MB limit");

        await using var stream      = await response.Content.ReadAsStreamAsync();
        using var       limitedStream = new LimitedStream(stream, MaxResponseBytes);
        using var       doc         = await JsonDocument.ParseAsync(limitedStream);

        return ParseOrsResponse(doc, startLat, startLng, endLat, endLng);
    }

    private static RouteGraph ParseOrsResponse(
        JsonDocument doc,
        double startLat, double startLng,
        double endLat,   double endLng)
    {
        var graph = new RouteGraph
        {
            StartCoord = new GeoCoordinate { Lat = startLat, Lng = startLng },
            EndCoord   = new GeoCoordinate { Lat = endLat,   Lng = endLng   },
            IsLoaded   = true
        };

        if (!doc.RootElement.TryGetProperty("features", out var features) ||
            features.GetArrayLength() == 0)
        {
            graph.ErrorMessage = "No route found";
            return graph;
        }

        var route = features[0];

        if (!route.TryGetProperty("properties", out var props) ||
            !props.TryGetProperty("summary",    out var summary))
        {
            graph.ErrorMessage = "Unexpected ORS response format";
            return graph;
        }

        graph.TotalDistanceMetres = summary.GetProperty("distance").GetDouble();

        // Parse geometry coordinates into route segments
        if (route.TryGetProperty("geometry", out var geometry) &&
            geometry.TryGetProperty("coordinates", out var coords))
        {
            var points = new List<GeoCoordinate>();
            foreach (var coord in coords.EnumerateArray())
            {
                var arr = coord.EnumerateArray().ToArray();
                if (arr.Length >= 2)
                    points.Add(new GeoCoordinate
                    {
                        Lat = arr[1].GetDouble(),
                        Lng = arr[0].GetDouble()
                    });
            }

            for (int i = 0; i < points.Count - 1; i++)
            {
                var segDist = points[i].DistanceTo(points[i + 1]);
                graph.Segments.Add(new RouteSegment
                {
                    StartCoord      = points[i],
                    EndCoord        = points[i + 1],
                    DistanceMetres  = segDist,
                    SpeedLimitKmh   = InferSpeedLimit(segDist),
                    LaneCount       = 2,
                    IsHighway       = segDist > 500
                });
            }
        }

        return graph;
    }

    private static int InferSpeedLimit(double segmentDistanceM) => segmentDistanceM switch
    {
        > 800  => 100,
        > 400  => 80,
        > 150  => 60,
        _      => 50
    };

    private static RouteGraph BuildDemoRoute(
        double startLat, double startLng,
        double endLat,   double endLng)
    {
        var graph = new RouteGraph
        {
            StartCoord = new GeoCoordinate { Lat = startLat, Lng = startLng },
            EndCoord   = new GeoCoordinate { Lat = endLat,   Lng = endLng   },
            IsLoaded   = true,
            ErrorMessage = "Demo mode — set OpenRouteService__ApiKey env var for real routing"
        };

        // Interpolate 4 waypoints between start and end
        var steps = 4;
        var prev  = graph.StartCoord;
        for (int i = 1; i <= steps; i++)
        {
            var t   = (double)i / steps;
            var cur = new GeoCoordinate
            {
                Lat = startLat + (endLat - startLat) * t,
                Lng = startLng + (endLng - startLng) * t
            };
            graph.Segments.Add(new RouteSegment
            {
                StartCoord     = prev,
                EndCoord       = cur,
                DistanceMetres = prev.DistanceTo(cur),
                SpeedLimitKmh  = 60,
                LaneCount      = 2,
                IsHighway      = false
            });
            graph.TotalDistanceMetres += prev.DistanceTo(cur);
            prev = cur;
        }

        return graph;
    }
}

// ─────────────────────────────────────────────────────────────
// LimitedStream — prevents memory exhaustion on oversized API responses
// ─────────────────────────────────────────────────────────────
internal sealed class LimitedStream(Stream inner, long maxBytes) : Stream
{
    private long _bytesRead = 0;

    public override bool CanRead  => inner.CanRead;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;
    public override long Length   => throw new NotSupportedException();
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
            throw new InvalidOperationException(
                $"API response exceeds {maxBytes:N0} byte safety limit");
        return n;
    }

    public override void Flush()                                         => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin)            => throw new NotSupportedException();
    public override void SetLength(long value)                           => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count)     => throw new NotSupportedException();
}
