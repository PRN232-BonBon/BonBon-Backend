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

### Tracking rules

Tracking is a two-sided view of the same car and is bound to the rental window:

- The customer (renter) watches the car approach the **pickup** point, and only
  from **30 minutes before `start_at`** while the booking is `Confirmed` or
  `ReadyForPickup`.
- The owner watches the car approach the **return** point, and only from
  **30 minutes before `end_at`** while the booking is `InProgress`.
- The pickup and return meeting points must be configured before that window
  opens. Once tracking opens the coordinates are locked; the only late write
  allowed is the first setup when a point is still missing.

The rules live in `BonBon.Business/Tracking/TrackingRules.cs` (30-minute lead time,
stage mapping, distance, 30 metre arrival radius) and are applied by
`TrackingService`, the controller and the SignalR hub alike.

### Setup endpoints (owner only)

`PUT /api/bookings/{bookingId}/tracking/device` with `{"deviceId": 123}`. The ID is
the Traccar Server device record ID; the identifier entered in Traccar Client is the
device's `uniqueId` and must match the registered Traccar device.

`PUT /api/bookings/{bookingId}/tracking/meeting-points` sets both meeting points:

```json
{
  "pickupLatitude": 10.7769,
  "pickupLongitude": 106.7009,
  "returnLatitude": 10.7792,
  "returnLongitude": 106.6991
}
```

Both endpoints answer `204 No Content`, or `409 Conflict` when the booking is not
`Confirmed`/`ReadyForPickup` or when tracking has already opened with the points
configured.

### Read endpoints

`GET /api/bookings/{bookingId}/tracking/status` returns the viewer's stage (`pickup`
for the customer, `return` for the owner), whether tracking may be shown
(`canTrack`), when it opens (`opensAt`, `deadline`, `secondsUntilOpen`) and what is
still missing (`state`: `Ready`, `WindowNotOpen`, `MeetingPointsMissing`,
`TrackerMissing`, `Inactive`). Clients use it to show a countdown instead of polling
the snapshot.

`GET /api/bookings/{bookingId}/tracking/snapshot` returns the latest coordinates,
the destination, the straight-line `distanceMeters` and `arrived` (true within 30
metres). It fails with `409 TRACKING_WINDOW_NOT_OPEN` before the window opens,
`409 TRACKING_NOT_ACTIVE` for the wrong stage, `409 MEETING_POINT_NOT_CONFIGURED`,
`409 TRACKER_NOT_CONFIGURED`, and `503 POSITION_NOT_AVAILABLE` when Traccar has not
sent a fix yet.

BonBon connects to Traccar's `/api/socket` WebSocket and stores every live fix in
`vehicle_location`. `PositionUpdated` events are published on `/hubs/tracking` with
SignalR only while a tracking window is open, and `JoinBooking(bookingId)` is
rejected outside the window, so clients receive new positions without polling. The
GPS update interval still depends on the Traccar Client's device and location
settings. For browser clients, connect to SignalR with the BonBon JWT as its access
token, join the booking, then fetch status/snapshot once to initialize the map.
