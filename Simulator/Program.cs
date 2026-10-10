using Simulator;

// Usage:
//   dotnet run --project Simulator -- [--api http://localhost:5080] [--drivers 5] [--riders 3]
//       [--center 21.1702,72.8311] [--tick-ms 500] [--meters-per-tick 150] [--timeout-s 180]
//
// Demo of contention: more riders than drivers, e.g. --drivers 3 --riders 8. Riders that cannot be matched
// immediately keep searching (expanding radius) and are matched as drivers finish trips, or end as NoDriverFound.

var options = SimulatorOptions.Parse(args);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var api = new ApiClient(options.Api);
Log.Info($"API {options.Api} · {options.Drivers} drivers · {options.Riders} riders · around ({options.Center.Latitude}, {options.Center.Longitude})");

// 1. Drivers sign up, connect to /hubs/driver, go online and start streaming their location.
var drivers = new List<SimulatedDriver>();
for (var i = 1; i <= options.Drivers; i++)
{
    var account = await api.SendAsync<Registered>(HttpMethod.Post, "/api/v1/drivers", new
    {
        name = $"Driver {i}",
        vehicleModel = i % 2 == 0 ? "Maruti Suzuki Dzire" : "Hyundai Aura",
        vehiclePlate = $"GJ05AB{1000 + i}",
    }, ct: cts.Token);

    var driver = new SimulatedDriver(i, account, api, options);
    await driver.ConnectAsync(cts.Token);
    drivers.Add(driver);
}

var driverLoops = drivers.Select(d => d.RunAsync(cts.Token)).ToList();
await Task.Delay(TimeSpan.FromSeconds(2), cts.Token); // let a few location pings land

// 2. Riders sign up, connect to /hubs/rider, and all request a ride at the same moment.
var riders = new List<SimulatedRider>();
for (var i = 1; i <= options.Riders; i++)
{
    var account = await api.SendAsync<Registered>(HttpMethod.Post, "/api/v1/riders", new { name = $"Rider {i}" }, ct: cts.Token);
    var rider = new SimulatedRider(i, account, api, options);
    await rider.ConnectAsync(cts.Token);
    riders.Add(rider);
}

Log.Info($"{riders.Count} riders requesting rides concurrently…");
RideDto[] results;
try
{
    results = await Task.WhenAll(riders.Select(r => r.RequestAndFollowAsync(cts.Token)));
}
catch (OperationCanceledException)
{
    Log.Bad("Cancelled.");
    return 1;
}

// 3. Summary + invariant check: no driver served two rides at overlapping times.
Console.WriteLine();
Log.Info("Summary");
foreach (var group in results.GroupBy(r => r.Status))
{
    Log.Info($"  {group.Key,-14} {group.Count()}");
}

var matched = riders.Where(r => r.AssignedAt is not null && r.FinalRide is not null).ToList();
if (matched.Count > 0)
{
    var waits = matched.Select(r => (r.AssignedAt!.Value - r.FinalRide!.RequestedAt).TotalSeconds).ToList();
    Log.Info($"  time to match: min {waits.Min():F1}s · avg {waits.Average():F1}s · max {waits.Max():F1}s");
}

var overlaps = riders
    .Where(r => r.DriverId is not null && r.AssignedAt is not null)
    .GroupBy(r => r.DriverId!.Value)
    .SelectMany(g =>
    {
        var trips = g.Select(r => (Start: r.AssignedAt!.Value, End: r.FinalRide?.CompletedAt ?? r.FinishedAt ?? DateTimeOffset.MaxValue))
                     .OrderBy(t => t.Start).ToList();
        return trips.Zip(trips.Skip(1)).Where(p => p.Second.Start < p.First.End);
    })
    .Count();

if (overlaps == 0) Log.Good($"  ✔ no driver was double-booked ({drivers.Sum(d => d.CompletedTrips)} trips completed by {drivers.Count} drivers)");
else Log.Bad($"  ✘ {overlaps} overlapping assignments detected");

// 4. Shut down cleanly: drivers go offline.
await cts.CancelAsync();
await Task.WhenAll(driverLoops.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
foreach (var driver in drivers) await driver.DisposeAsync();
foreach (var rider in riders) await rider.DisposeAsync();
return overlaps == 0 ? 0 : 2;
