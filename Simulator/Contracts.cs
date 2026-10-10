namespace Simulator
{
    // Client-side copies of the wire contracts (a mobile app would generate these from the OpenAPI document).

    public sealed record Location(double Latitude, double Longitude);

    public sealed record Registered(Guid Id, string Role, string AccessToken);

    public sealed record DriverStateDto(Guid DriverId, string Status, Location? Location, Guid? CurrentRideId);

    public sealed record AssignedDriverDto(Guid Id, string Name, string VehicleModel, string VehiclePlate, double? DistanceAtAssignmentMeters);

    public sealed record RideDto(
        Guid Id,
        string Status,
        Guid RiderId,
        Location Pickup,
        Location Dropoff,
        decimal EstimatedFare,
        string Currency,
        AssignedDriverDto? Driver,
        int MatchAttempts,
        DateTimeOffset RequestedAt,
        DateTimeOffset? AssignedAt,
        DateTimeOffset? CompletedAt,
        int Version);

    public sealed record RideCancelledDto(Guid RideId, string Reason);

    public sealed record DriverLocationDto(Guid RideId, Guid DriverId, double Latitude, double Longitude, DateTimeOffset At);

    public static class Geo
    {
        private const double MetersPerDegreeLat = 111_195d;

        public static double DistanceMeters(Location a, Location b)
        {
            static double Rad(double d) => d * Math.PI / 180;
            var dLat = Rad(b.Latitude - a.Latitude);
            var dLon = Rad(b.Longitude - a.Longitude);
            var h = Math.Pow(Math.Sin(dLat / 2), 2) + Math.Cos(Rad(a.Latitude)) * Math.Cos(Rad(b.Latitude)) * Math.Pow(Math.Sin(dLon / 2), 2);
            return 2 * 6_371_008.8 * Math.Asin(Math.Min(1, Math.Sqrt(h)));
        }

        /// <summary>Moves from <paramref name="from"/> towards <paramref name="to"/> by at most <paramref name="meters"/>.</summary>
        public static Location MoveTowards(Location from, Location to, double meters)
        {
            var distance = DistanceMeters(from, to);
            if (distance <= meters || distance < 1) return to;
            var f = meters / distance;
            return new Location(from.Latitude + (to.Latitude - from.Latitude) * f, from.Longitude + (to.Longitude - from.Longitude) * f);
        }

        public static Location RandomNear(Location center, double maxMeters, Random random)
        {
            var r = maxMeters * Math.Sqrt(random.NextDouble());
            var theta = random.NextDouble() * 2 * Math.PI;
            var dLat = r * Math.Cos(theta) / MetersPerDegreeLat;
            var dLon = r * Math.Sin(theta) / (MetersPerDegreeLat * Math.Cos(center.Latitude * Math.PI / 180));
            return new Location(center.Latitude + dLat, center.Longitude + dLon);
        }
    }
}
