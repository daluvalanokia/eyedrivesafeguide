namespace EyeDriveGuide.Models
{
    public class RouteGraph
    {
        public List<RouteSegment> Segments { get; set; } = new();
        public List<RouteEvent> Events { get; set; } = new();
        public double TotalDistanceMetres { get; set; }
        public GeoCoordinate StartCoord { get; set; } = new();
        public GeoCoordinate EndCoord { get; set; } = new();
        public bool IsLoaded { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class RouteSegment
    {
        public int Index { get; set; }
        public GeoCoordinate StartCoord { get; set; } = new();
        public GeoCoordinate EndCoord { get; set; } = new();
        public double DistanceMetres { get; set; }
        public double SpeedLimitKmh { get; set; } = 80;
        public int LaneCount { get; set; } = 2;
        public bool IsHighway { get; set; }
        public bool HasConstruction { get; set; }
        public double? AdvisoryRampSpeedKmh { get; set; }
    }

    public class RouteEvent
    {
        public RouteEventType Type { get; set; }
        public GeoCoordinate Coord { get; set; } = new();
        public GeoCoordinate? LookaheadOneMile { get; set; }
        public GeoCoordinate? Lookahead600M { get; set; }
        public int? ExitLaneIndex { get; set; }
        public int? TotalLanes { get; set; }
        public string? Description { get; set; }
        public double? AdvisoryRampSpeedKmh { get; set; }
    }

    public enum RouteEventType
    {
        Merge,
        Exit,
        Turn,
        Construction,
        OnRamp,
        OffRamp
    }

    public class GeoCoordinate
    {
        public double Lat { get; set; }
        public double Lng { get; set; }

        public double DistanceTo(GeoCoordinate other)
        {
            const double R = 6371000;
            var dLat = (other.Lat - Lat) * Math.PI / 180;
            var dLon = (other.Lng - Lng) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                  + Math.Cos(Lat * Math.PI / 180) * Math.Cos(other.Lat * Math.PI / 180)
                  * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }
    }
}
