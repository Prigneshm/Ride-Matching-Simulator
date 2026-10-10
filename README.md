# Ride Matching Simulator

A separate console solution that acts like the driver and rider mobile apps for the Ride Matching API. It talks to the
API only over HTTP and SignalR (`Microsoft.AspNetCore.SignalR.Client`) and does not reference any server project.

- **Drivers** register, connect to `/hubs/driver`, go online and stream their location (`StreamLocation`). When a ride
  is assigned they drive to the pickup, then call `arrive`, `start` and `complete`.
- **Riders** register, connect to `/hubs/rider`, request rides at the same moment and follow them through pushes
  (status changes and live driver distance).
- **At the end** it prints a summary and checks that no driver served two rides at overlapping times.

## Run

Start the API first (see the RideMatchingAPI solution, e.g. `docker compose up --build`), then:

```bash
dotnet run --project Simulator -- --drivers 3 --riders 8
```

| Option | Default | Meaning |
|---|---|---|
| `--api` | `http://localhost:5080` | API base URL |
| `--drivers` | 5 | Number of simulated drivers |
| `--riders` | 3 | Number of riders requesting at the same moment |
| `--center` | `21.1702,72.8311` | Centre of the simulated area (lat,lon) |
| `--tick-ms` | 500 | Simulation tick |
| `--meters-per-tick` | 150 | Car movement per tick (sped up for demos) |
| `--timeout-s` | 180 | How long a rider waits for the ride to finish |
