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

## Vehicle tracking from the renter's phone

Tracking does not use a hardware tracker. The renter's phone uploads its GPS
position while the car is rented, and the owner watches it live.

### Tracking rules

- Tracking switches on automatically when the handover is confirmed:
  `POST /api/bookings/{id}/pickup` moves the booking to `InProgress`, and
  `TrackingRules.IsActive` treats exactly that status as "tracking on".
- It switches off automatically as soon as the booking leaves `InProgress`
  (`return`, `cancel` and so on), because positions are only accepted and only
  published in that status.
- Only the **owner** can read tracking data. The renter never sees a map; their
  phone is the position source.
- The return meeting point is the reference used for `distanceMeters` / `arrived`.
  The owner must set it; it is locked once the rental starts, except for the first
  setup when it is still missing.

The rules live in `BonBon.Business/Common/TrackingRules.cs`.

### Endpoints

`PUT /api/bookings/{bookingId}/tracking/meeting-points` — owner only:

```json
{
  "pickupLatitude": 10.7769,
  "pickupLongitude": 106.7009,
  "returnLatitude": 10.7792,
  "returnLongitude": 106.6991
}
```

`409 Conflict` when the booking is not in a configurable status
(`Confirmed`, `ReadyForPickup`, `InProgress`) or when the return point is already
set while the rental is running.

`POST /api/bookings/{bookingId}/tracking/positions` — called by the renter's phone,
only while the booking is `InProgress`:

```json
{
  "latitude": 10.7787,
  "longitude": 106.7001,
  "accuracyMeters": 8,
  "recordedAt": "2026-11-01T09:41:00+07:00"
}
```

`recordedAt` is optional (server time is used, and future timestamps are clamped).
Answers `204 No Content`. `409 TRACKING_NOT_ACTIVE` when the rental is not running.

`GET /api/bookings/{bookingId}/tracking/status` — owner only. Returns `state`
(`Ready`, `MeetingPointsMissing`, `Inactive`), `canTrack`, the rental `deadline`
and `meetingPointsConfigured`.

`GET /api/bookings/{bookingId}/tracking/snapshot` — owner only. Returns the latest
coordinates, the return destination, the straight-line `distanceMeters` and
`arrived` (true within 30 metres). Fails with `409 TRACKING_NOT_ACTIVE` outside the
rental, `409 MEETING_POINT_NOT_CONFIGURED`, `403 FORBIDDEN` for the renter or a
stranger, and `503 POSITION_NOT_AVAILABLE` before the first phone fix.

### Live updates

Every accepted fix is stored in `vehicle_location` with `source = 'Phone'` and
published on `/hubs/tracking` as `PositionUpdated` to the booking group, so the
owner's map updates without polling. `JoinBooking(bookingId)` is rejected for
anyone but the owner of a running rental. For browser clients, connect to SignalR
with the BonBon JWT as its access token, join the booking, then fetch
status/snapshot once to initialize the map.

Apply `database/migrations/009_phone_tracking.sql` to an existing database to drop
the old Traccar device column and add the phone fix index.
