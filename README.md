# BonBon Backend

Backend repository for the BonBon PRN232 project.

## Stack

- C# and .NET 8
- ASP.NET Core Web API
- N-Layer Architecture
- PostgreSQL 16
- Docker Compose deployment for the demo VPS

## Projects

- `BonBon.API`
- `BonBon.Business`
- `BonBon.DataAccess`
- `BonBon.Entities`

No business features or database integration are implemented yet. The current API
exposes `GET /health` so the deployment can be verified before feature work begins.

## Demo deployment

The Compose stack is intentionally small for the 2 vCPU / 2 GB RAM demo VPS:

- `postgres`: PostgreSQL 16 Alpine, limited to 512 MB, exposed only on localhost
- `api`: ASP.NET Core 8, limited to 256 MB, exposed on port 8080

Create the deployment environment file and choose a strong database password:

```bash
cp .env.example .env
nano .env
docker compose up -d --build
docker compose ps
curl http://127.0.0.1:8080/health
```

The SQL schema in `database/migrations/001_initial_schema.sql` is applied only when
PostgreSQL initializes an empty `postgres_data` volume. Do not remove that volume on
a running environment unless the database is intentionally being recreated.

## Vehicle tracking with Traccar Client

Traccar Client reports GPS coordinates to a Traccar Server. Configure the app with
the public Traccar server URL and the device identifier, then set `TRACCAR_BASE_URL`
and `TRACCAR_API_TOKEN` for the BonBon API. Apply
`database/migrations/003_vehicle_tracking.sql` to an existing database once (fresh
Compose databases also load it during initialization). The token is a Traccar API
key and should be kept secret.

The owner links the Traccar Server's numeric device ID to a booking with
`PUT /api/bookings/{bookingId}/tracking/device` and `{"deviceId": 123}`. The ID is
the Traccar Server device record ID; the identifier entered in Traccar Client is the
device's `uniqueId` and must match the registered Traccar device.

The owner sets the pickup and return meeting coordinates with
`PUT /api/bookings/{bookingId}/tracking/meeting-points`:

```json
{
  "pickupLatitude": 10.7769,
  "pickupLongitude": 106.7009,
  "returnLatitude": 10.7792,
  "returnLongitude": 106.6991
}
```

The authenticated customer calls `GET /api/bookings/{bookingId}/tracking/snapshot`
while status is `ReadyForPickup` to get the car's latest coordinates and distance
to the pickup point. The owner calls the same endpoint while status is `InProgress`
to see the car's distance to the return point. Distance is calculated in a straight
line from GPS coordinates; `arrived` becomes true within 30 meters. BonBon connects to Traccar's `/api/socket` WebSocket,
stores each live fix in `vehicle_location`, and publishes `PositionUpdated` events
on `/hubs/tracking` with SignalR; clients call `JoinBooking(bookingId)` and receive
new positions without polling. The GPS update interval still depends on the
Traccar Client's device and location settings. For browser clients, connect to
SignalR with the BonBon JWT as its access token, join the booking, then fetch the
snapshot endpoint once to initialize the map.
