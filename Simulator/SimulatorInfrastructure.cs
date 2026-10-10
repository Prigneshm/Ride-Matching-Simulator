using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Simulator
{
    public sealed class SimulatorOptions
    {
        public Uri Api { get; init; } = new("http://localhost:5080");
        public int Drivers { get; init; } = 5;
        public int Riders { get; init; } = 3;
        public Location Center { get; init; } = new(21.1702, 72.8311); // Surat
        public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(500);
        /// <summary>Meters a car moves per tick (sped up so a demo trip takes ~20 seconds).</summary>
        public double MetersPerTick { get; init; } = 150;
        public TimeSpan RiderTimeout { get; init; } = TimeSpan.FromMinutes(3);

        public static SimulatorOptions Parse(string[] args)
        {
            string? Get(string name)
            {
                var i = Array.IndexOf(args, "--" + name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }

            static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
            var defaults = new SimulatorOptions();
            var center = Get("center")?.Split(',');

            return new SimulatorOptions
            {
                Api = Get("api") is { } api ? new Uri(api) : defaults.Api,
                Drivers = Get("drivers") is { } d ? int.Parse(d, CultureInfo.InvariantCulture) : defaults.Drivers,
                Riders = Get("riders") is { } r ? int.Parse(r, CultureInfo.InvariantCulture) : defaults.Riders,
                Center = center is { Length: 2 } ? new Location(D(center[0]), D(center[1])) : defaults.Center,
                Tick = Get("tick-ms") is { } t ? TimeSpan.FromMilliseconds(D(t)) : defaults.Tick,
                MetersPerTick = Get("meters-per-tick") is { } m ? D(m) : defaults.MetersPerTick,
                RiderTimeout = Get("timeout-s") is { } s ? TimeSpan.FromSeconds(D(s)) : defaults.RiderTimeout,
            };
        }
    }

    /// <summary>Thin REST client (what the mobile app's API layer would be).</summary>
    public sealed class ApiClient(Uri baseAddress) : IDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly HttpClient _http = new() { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };

        public Uri BaseAddress => baseAddress;

        public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, string? token = null, CancellationToken ct = default)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var problem = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"{method} {path} → {(int)response.StatusCode}: {problem}");
            }
            return (await response.Content.ReadFromJsonAsync<T>(Json, ct))!;
        }

        public void Dispose() => _http.Dispose();
    }

    public static class Log
    {
        private static readonly Lock Gate = new();
        private static readonly DateTimeOffset Start = DateTimeOffset.UtcNow;

        public static void Driver(int n, string message) => Write(ConsoleColor.Cyan, $"D{n}", message);
        public static void Rider(int n, string message) => Write(ConsoleColor.Yellow, $"R{n}", message);
        public static void Info(string message) => Write(ConsoleColor.Gray, "SIM", message);
        public static void Good(string message) => Write(ConsoleColor.Green, "SIM", message);
        public static void Bad(string message) => Write(ConsoleColor.Red, "SIM", message);

        private static void Write(ConsoleColor color, string who, string message)
        {
            lock (Gate)
            {
                var elapsed = DateTimeOffset.UtcNow - Start;
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"{elapsed:mm\\:ss\\.f} ");
                Console.ForegroundColor = color;
                Console.Write($"[{who,-3}] ");
                Console.ResetColor();
                Console.WriteLine(message);
            }
        }
    }
}
