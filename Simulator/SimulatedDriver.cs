using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;

namespace Simulator
{
    /// <summary>
    /// A driver app: one SignalR connection for presence + a client-to-server location stream, ride assignments
    /// pushed by the server, and REST calls for trip milestones (arrive/start/complete).
    /// </summary>
    public sealed class SimulatedDriver : IAsyncDisposable
    {
        private enum Phase { Idle, ToPickup, AtPickup, ToDropoff }

        private readonly int _n;
        private readonly Registered _account;
        private readonly ApiClient _api;
        private readonly SimulatorOptions _options;
        private readonly Random _random;
        private readonly HubConnection _hub;
        private readonly Lock _gate = new();

        private Channel<Location> _locations = Channel.CreateUnbounded<Location>();
        private Location _position;
        private Location _wanderTarget;
        private Phase _phase = Phase.Idle;
        private RideDto? _ride;
        private int _waitTicks;
        private volatile bool _mustGoOnline;

        public SimulatedDriver(int n, Registered account, ApiClient api, SimulatorOptions options)
        {
            _n = n;
            _account = account;
            _api = api;
            _options = options;
            _random = new Random(n * 7919);
            _position = Geo.RandomNear(options.Center, 2_500, _random);
            _wanderTarget = _position;

            _hub = new HubConnectionBuilder()
                .WithUrl(new Uri(api.BaseAddress, "/hubs/driver"), o => o.AccessTokenProvider = () => Task.FromResult<string?>(account.AccessToken))
                .WithAutomaticReconnect()
                .Build();

            _hub.On<RideDto>("RideAssigned", OnRideAssigned);
            _hub.On<RideDto>("RideUpdated", ride => Log.Driver(_n, $"ride {Short(ride.Id)} is now {ride.Status}"));
            _hub.On<RideCancelledDto>("RideCancelled", OnRideCancelled);
            _hub.On<string>("LocationRejected", reason =>
            {
                Log.Driver(_n, $"location rejected ({reason}); going online again");
                _mustGoOnline = true;
            });
            _hub.Reconnected += async _ =>
            {
                // A new connection: the previous upload stream died with the old one.
                Log.Driver(_n, "reconnected; restarting location stream");
                await StartLocationStreamAsync(CancellationToken.None);
                _mustGoOnline = true;
            };
        }

        public Guid Id => _account.Id;
        public int CompletedTrips { get; private set; }

        public async Task ConnectAsync(CancellationToken ct)
        {
            await _hub.StartAsync(ct);
            var state = await _hub.InvokeAsync<DriverStateDto>("GoOnline", _position, ct);
            Log.Driver(_n, $"online via SignalR as {state.Status} at {Fmt(_position)}");
            await StartLocationStreamAsync(ct);
        }

        public async Task RunAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(_options.Tick);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (_mustGoOnline)
                    {
                        _mustGoOnline = false;
                        await _hub.InvokeAsync<DriverStateDto>("GoOnline", _position, ct);
                    }
                    await StepAsync(ct);
                    _locations.Writer.TryWrite(_position);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Driver(_n, $"error: {ex.Message}");
                }
            }
        }

        private async Task StepAsync(CancellationToken ct)
        {
            Phase phase;
            RideDto? ride;
            lock (_gate)
            {
                phase = _phase;
                ride = _ride;
            }

            switch (phase)
            {
                case Phase.Idle:
                    // Cruise around the city while waiting for a request.
                    if (Geo.DistanceMeters(_position, _wanderTarget) < 20) _wanderTarget = Geo.RandomNear(_options.Center, 2_500, _random);
                    _position = Geo.MoveTowards(_position, _wanderTarget, _options.MetersPerTick / 3);
                    break;

                case Phase.ToPickup:
                    _position = Geo.MoveTowards(_position, ride!.Pickup, _options.MetersPerTick);
                    if (Geo.DistanceMeters(_position, ride.Pickup) < 1)
                    {
                        await _api.SendAsync<RideDto>(HttpMethod.Post, $"/api/v1/rides/{ride.Id}/arrive", token: _account.AccessToken, ct: ct);
                        Log.Driver(_n, $"arrived at pickup for {Short(ride.Id)}");
                        SetPhase(Phase.AtPickup, ride);
                        _waitTicks = 3;
                    }
                    break;

                case Phase.AtPickup:
                    if (--_waitTicks <= 0)
                    {
                        await _api.SendAsync<RideDto>(HttpMethod.Post, $"/api/v1/rides/{ride!.Id}/start", token: _account.AccessToken, ct: ct);
                        Log.Driver(_n, $"trip {Short(ride.Id)} started → {Geo.DistanceMeters(_position, ride.Dropoff):F0} m to dropoff");
                        SetPhase(Phase.ToDropoff, ride);
                    }
                    break;

                case Phase.ToDropoff:
                    _position = Geo.MoveTowards(_position, ride!.Dropoff, _options.MetersPerTick);
                    if (Geo.DistanceMeters(_position, ride.Dropoff) < 1)
                    {
                        await _api.SendAsync<RideDto>(HttpMethod.Post, $"/api/v1/rides/{ride.Id}/complete", token: _account.AccessToken, ct: ct);
                        CompletedTrips++;
                        Log.Driver(_n, $"trip {Short(ride.Id)} completed; available again");
                        SetPhase(Phase.Idle, null);
                        _wanderTarget = _position;
                    }
                    break;
            }
        }

        private void OnRideAssigned(RideDto ride)
        {
            lock (_gate)
            {
                if (_phase != Phase.Idle)
                {
                    // Must never happen: the backend guarantees a driver has at most one active ride.
                    Log.Driver(_n, $"!!! assigned {Short(ride.Id)} while busy with {Short(_ride!.Id)}");
                    return;
                }
                _ride = ride;
                _phase = Phase.ToPickup;
            }
            Log.Driver(_n, $"assigned ride {Short(ride.Id)}: pickup {Geo.DistanceMeters(_position, ride.Pickup):F0} m away, fare {ride.EstimatedFare} {ride.Currency}");
        }

        private void OnRideCancelled(RideCancelledDto cancelled)
        {
            lock (_gate)
            {
                if (_ride?.Id != cancelled.RideId) return;
                _ride = null;
                _phase = Phase.Idle;
            }
            Log.Driver(_n, $"ride {Short(cancelled.RideId)} cancelled: {cancelled.Reason}");
        }

        private void SetPhase(Phase phase, RideDto? ride)
        {
            lock (_gate)
            {
                _phase = phase;
                _ride = ride;
            }
        }

        private async Task StartLocationStreamAsync(CancellationToken ct)
        {
            _locations.Writer.TryComplete();
            _locations = Channel.CreateUnbounded<Location>();
            // Client-to-server streaming: the server's StreamLocation(IAsyncEnumerable<...>) consumes this channel.
            await _hub.SendAsync("StreamLocation", _locations.Reader, ct);
        }

        public async ValueTask DisposeAsync()
        {
            _locations.Writer.TryComplete();
            try
            {
                if (_hub.State == HubConnectionState.Connected && _phase == Phase.Idle)
                    await _hub.InvokeAsync<DriverStateDto>("GoOffline");
            }
            catch (Exception ex)
            {
                Log.Driver(_n, $"could not go offline: {ex.Message}");
            }
            await _hub.DisposeAsync();
        }

        private static string Short(Guid id) => id.ToString("N")[^6..];
        private static string Fmt(Location l) => $"({l.Latitude:F5}, {l.Longitude:F5})";
    }
}
