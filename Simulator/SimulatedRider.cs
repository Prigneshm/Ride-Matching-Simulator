using Microsoft.AspNetCore.SignalR.Client;

namespace Simulator
{
    /// <summary>A rider app: requests a ride over REST, then follows it purely through SignalR pushes.</summary>
    public sealed class SimulatedRider : IAsyncDisposable
    {
        private static readonly HashSet<string> Terminal = ["Completed", "Cancelled", "NoDriverFound"];

        private readonly int _n;
        private readonly Registered _account;
        private readonly ApiClient _api;
        private readonly SimulatorOptions _options;
        private readonly HubConnection _hub;
        private readonly TaskCompletionSource<RideDto> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Random _random;

        private readonly Lock _gate = new();
        private int _lastVersion;
        private string _lastStatus = string.Empty;
        private int _locationPings;
        private RideDto? _ride;

        public SimulatedRider(int n, Registered account, ApiClient api, SimulatorOptions options)
        {
            _n = n;
            _account = account;
            _api = api;
            _options = options;
            _random = new Random(n * 104_729);

            _hub = new HubConnectionBuilder()
                .WithUrl(new Uri(api.BaseAddress, "/hubs/rider"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(account.AccessToken))
                .WithAutomaticReconnect()
                .Build();

            _hub.On<RideDto>("RideUpdated", OnRideUpdated);
            _hub.On<DriverLocationDto>("DriverLocationUpdated", OnDriverLocation);
        }

        public RideDto? FinalRide { get; private set; }
        public DateTimeOffset? AssignedAt { get; private set; }
        public DateTimeOffset? FinishedAt { get; private set; }
        public Guid? DriverId { get; private set; }

        public async Task ConnectAsync(CancellationToken ct) => await _hub.StartAsync(ct);

        public async Task<RideDto> RequestAndFollowAsync(CancellationToken ct)
        {
            var pickup = Geo.RandomNear(_options.Center, 2_000, _random);
            Location dropoff;
            do dropoff = Geo.RandomNear(pickup, 3_000, _random);
            while (Geo.DistanceMeters(pickup, dropoff) < 800);

            _ride = await _api.SendAsync<RideDto>(HttpMethod.Post, "/api/v1/rides",
                new { pickup, dropoff }, _account.AccessToken, ct);
            Log.Rider(_n, $"requested ride {Short(_ride.Id)} ({Geo.DistanceMeters(pickup, dropoff):F0} m trip) → 202 {_ride.Status}");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RiderTimeout);
            try
            {
                FinalRide = await _finished.Task.WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Push missed or ride still going: fall back to a GET, like an app would after reconnecting.
                FinalRide = await _api.SendAsync<RideDto>(HttpMethod.Get, $"/api/v1/rides/{_ride.Id}", token: _account.AccessToken, ct: ct);
                Log.Rider(_n, $"timed out waiting; GET says {FinalRide.Status}");
            }
            FinishedAt ??= DateTimeOffset.UtcNow;
            return FinalRide;
        }

        private void OnRideUpdated(RideDto ride)
        {
            lock (_gate)
            {
                // Pushes can arrive out of order or twice (reconnects): Version makes handling idempotent.
                if (ride.Version <= _lastVersion) return;
                _lastVersion = ride.Version;
                _lastStatus = ride.Status;
            }

            switch (ride.Status)
            {
                case "Searching":
                    Log.Rider(_n, $"still searching… (attempt {ride.MatchAttempts})");
                    break;
                case "DriverAssigned":
                    AssignedAt = ride.AssignedAt;
                    DriverId = ride.Driver?.Id;
                    Log.Rider(_n, $"matched with {ride.Driver?.Name} ({ride.Driver?.VehicleModel}, {ride.Driver?.VehiclePlate}), " +
                                  $"{ride.Driver?.DistanceAtAssignmentMeters:F0} m away, after {(ride.AssignedAt - ride.RequestedAt)?.TotalSeconds:F1}s");
                    break;
                default:
                    Log.Rider(_n, $"ride is {ride.Status}");
                    break;
            }

            if (Terminal.Contains(ride.Status))
            {
                FinishedAt = DateTimeOffset.UtcNow;
                _finished.TrySetResult(ride);
            }
        }

        private void OnDriverLocation(DriverLocationDto location)
        {
            if (_ride is null || Interlocked.Increment(ref _locationPings) % 8 != 1) return; // keep the console readable
            var here = new Location(location.Latitude, location.Longitude);
            var target = _lastStatus == "InProgress" ? _ride.Dropoff : _ride.Pickup;
            var label = _lastStatus == "InProgress" ? "to dropoff" : "to pickup";
            Log.Rider(_n, $"live driver position: {Geo.DistanceMeters(here, target):F0} m {label}");
        }

        public async ValueTask DisposeAsync() => await _hub.DisposeAsync();

        private static string Short(Guid id) => id.ToString("N")[^6..];
    }
}
