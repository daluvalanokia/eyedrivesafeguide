using EyeDriveGuide.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace EyeDriveGuide.Services
{
    public class RouteService
    {
        private readonly HttpClient _http;
        private readonly IMemoryCache _cache;
        private readonly IConfiguration _config;
        private readonly ILogger<RouteService> _logger;

        public RouteService(HttpClient http, IMemoryCache cache, IConfiguration config, ILogger<RouteService> logger)
        {
            _http = http;
            _cache = cache;
            _config = config;
            _logger = logger;
        }

        public async Task<RouteGraph> LoadRouteAsync(double startLat, double startLng, double endLat, double endLng)
        {
            var cacheKey = $"route:{startLat:F4},{startLng:F4}->{endLat:F4},{endLng:F4}";
            if (_cache.TryGetValue(cacheKey, out RouteGraph? cached) && cached != null)
                return cached;

            var apiKey = _config["OpenRouteService:ApiKey"];
            RouteGraph graph;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                graph = BuildDemoRoute(startLat, startLng, endLat, endLng);
            }
            else
            {
                try
                {
                    graph = await FetchFromOpenRouteServiceAsync(startLat, startLng, endLat, endLng, apiKey);
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
            double startLat, double startLng, double endLat, double endLng, string apiKey)
        {
            var url = $"https://api.openrouteservice.org/v2/directions/driving-car" +
                      $"?api_key={apiKey}" +
                      $"&start={startLng},{startLat}&end={endLng},{endLat}";

            var response = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(response);

            var graph = new RouteGraph
            {
                StartCoord = new GeoCoordinate { Lat = startLat, Lng = startLng },
                EndCoord = new GeoCoordinate { Lat = endLat, Lng = endLng },
                IsLoaded = true
            };

            var features = doc.RootElement.GetProperty("features");
            if (features.GetArrayLength() == 0)
            {
                graph.ErrorMessage = "No route found";
                return graph;
            }

            var route = features[0];
            var summary = route.GetProperty("properties").GetProperty("summary");
            graph.TotalDistanceMetres = summary.GetProperty("distance").GetDouble();

            var coords = route.GetProperty("geometry").GetProperty("coordinates");
            var segments = route.GetProperty("properties").GetProperty("segments");

            int segIdx = 0;
            foreach (var seg in segments.EnumerateArray())
            {
                var steps = seg.GetProperty("steps");
                foreach (var step in steps.EnumerateArray())
                {
                    var wayPoints = step.GetProperty("way_points");
                    var startWp = wayPoints[0].GetInt32();
                    var endWp = wayPoints[1].GetInt32();

                    if (startWp >= coords.GetArrayLength() || endWp >= coords.GetArrayLength())
                        continue;

                    var startCoord = coords[startWp];
                    var endCoord = coords[endWp];

                    var segment = new RouteSegment
                    {
                        Index = segIdx++,
                        StartCoord = new GeoCoordinate
                        {
                            Lat = startCoord[1].GetDouble(),
                            Lng = startCoord[0].GetDouble()
                        },
                        EndCoord = new GeoCoordinate
                        {
                            Lat = endCoord[1].GetDouble(),
                            Lng = endCoord[0].GetDouble()
                        },
                        DistanceMetres = step.GetProperty("distance").GetDouble(),
                        SpeedLimitKmh = InferSpeedLimit(step),
                        LaneCount = InferLaneCount(step),
                        IsHighway = IsHighwayStep(step),
                        HasConstruction = IsConstructionStep(step)
                    };
                    graph.Segments.Add(segment);

                    var eventType = InferEventType(step);
                    if (eventType.HasValue)
                    {
                        graph.Events.Add(new RouteEvent
                        {
                            Type = eventType.Value,
                            Coord = segment.EndCoord,
                            Description = step.TryGetProperty("instruction", out var instr) ? instr.GetString() : null,
                            TotalLanes = segment.LaneCount,
                            ExitLaneIndex = segment.LaneCount > 0 ? segment.LaneCount - 1 : 0
                        });
                    }
                }
            }

            return graph;
        }

        private static double InferSpeedLimit(JsonElement step)
        {
            if (step.TryGetProperty("name", out var name))
            {
                var n = name.GetString()?.ToLower() ?? "";
                if (n.Contains("highway") || n.Contains("interstate") || n.Contains("freeway"))
                    return 105;
                if (n.Contains("motorway")) return 120;
            }
            var dist = step.TryGetProperty("distance", out var d) ? d.GetDouble() : 0;
            return dist > 2000 ? 80 : 50;
        }

        private static int InferLaneCount(JsonElement step)
        {
            if (step.TryGetProperty("name", out var name))
            {
                var n = name.GetString()?.ToLower() ?? "";
                if (n.Contains("highway") || n.Contains("interstate") || n.Contains("motorway"))
                    return 3;
            }
            return 2;
        }

        private static bool IsHighwayStep(JsonElement step)
        {
            if (step.TryGetProperty("name", out var name))
            {
                var n = name.GetString()?.ToLower() ?? "";
                return n.Contains("highway") || n.Contains("interstate") ||
                       n.Contains("motorway") || n.Contains("freeway");
            }
            return false;
        }

        private static bool IsConstructionStep(JsonElement step)
        {
            if (step.TryGetProperty("name", out var name))
            {
                var n = name.GetString()?.ToLower() ?? "";
                if (n.Contains("construction") || n.Contains("work zone") || n.Contains("roadwork"))
                    return true;
            }
            if (step.TryGetProperty("type", out var typeEl) && typeEl.GetInt32() == 13)
                return true;
            return false;
        }

        private static RouteEventType? InferEventType(JsonElement step)
        {
            if (!step.TryGetProperty("type", out var typeEl)) return null;
            return typeEl.GetInt32() switch
            {
                1 => RouteEventType.Turn,
                10 => RouteEventType.Merge,
                11 => RouteEventType.OnRamp,
                12 => RouteEventType.OffRamp,
                _ => null
            };
        }

        private static RouteGraph BuildDemoRoute(double startLat, double startLng, double endLat, double endLng)
        {
            var graph = new RouteGraph
            {
                StartCoord = new GeoCoordinate { Lat = startLat, Lng = startLng },
                EndCoord = new GeoCoordinate { Lat = endLat, Lng = endLng },
                IsLoaded = true,
                ErrorMessage = "Demo mode — configure OpenRouteService:ApiKey for real routing"
            };

            var midLat = (startLat + endLat) / 2;
            var midLng = (startLng + endLng) / 2;
            var mid2Lat = (midLat + endLat) / 2;
            var mid2Lng = (midLng + endLng) / 2;

            graph.Segments.Add(new RouteSegment
            {
                Index = 0,
                StartCoord = new GeoCoordinate { Lat = startLat, Lng = startLng },
                EndCoord = new GeoCoordinate { Lat = midLat, Lng = midLng },
                DistanceMetres = 5000,
                SpeedLimitKmh = 50,
                LaneCount = 2,
                IsHighway = false
            });

            graph.Segments.Add(new RouteSegment
            {
                Index = 1,
                StartCoord = new GeoCoordinate { Lat = midLat, Lng = midLng },
                EndCoord = new GeoCoordinate { Lat = mid2Lat, Lng = mid2Lng },
                DistanceMetres = 2500,
                SpeedLimitKmh = 100,
                LaneCount = 3,
                IsHighway = true,
                HasConstruction = true
            });

            graph.Segments.Add(new RouteSegment
            {
                Index = 2,
                StartCoord = new GeoCoordinate { Lat = mid2Lat, Lng = mid2Lng },
                EndCoord = new GeoCoordinate { Lat = endLat, Lng = endLng },
                DistanceMetres = 2500,
                SpeedLimitKmh = 100,
                LaneCount = 3,
                IsHighway = true
            });

            graph.Events.Add(new RouteEvent
            {
                Type = RouteEventType.OnRamp,
                Coord = new GeoCoordinate { Lat = midLat, Lng = midLng },
                TotalLanes = 3,
                ExitLaneIndex = 2,
                Description = "Highway on-ramp (demo)"
            });

            graph.Events.Add(new RouteEvent
            {
                Type = RouteEventType.OffRamp,
                Coord = new GeoCoordinate { Lat = endLat, Lng = endLng },
                TotalLanes = 3,
                ExitLaneIndex = 2,
                Description = "Take exit (demo)"
            });

            graph.TotalDistanceMetres = 10000;
            return graph;
        }
    }
}
