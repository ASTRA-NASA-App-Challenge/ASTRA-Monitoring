# ASTRA Monitoring

ASP.NET Core monitoring API with SQLite application data and private Supabase photo storage. The only stored crew identity is the six-digit PIN string, including leading zeroes. No web usernames or account credentials are stored. Device IDs, session IDs and measurement IDs describe those resources rather than additional crew identities.

## Configuration

Run `dotnet run`. HTTP port 5000 remains available for the unchanged Uno/camera firmware; port 5191 remains available for the web integration. Configure the listening addresses through `Urls` or `--urls` as needed. The firmware sends plain HTTP, so do not force its existing connection through an HTTPS redirect. Use a trusted local network for that link.

Both hosts load their own ignored `.env` files. Precedence is: appsettings defaults, .env, development user secrets, environment variables, command-line arguments. `.env.example` contains placeholders, never usable secret keys.

- `ConnectionStrings__DefaultConnection`: SQLite, e.g. `Data Source=monitoring.db;Foreign Keys=True`. Relative paths resolve against the host project directory.
- `MonitoringApi__WebApiKey`: the same random secret (at least 32 characters) on Monitoring and Web. Web sends it server-side in `X-Api-Key`.
- `Supabase__Url`, `Supabase__Bucket`, `Supabase__ServiceRoleKey`: Monitoring's storage configuration. The service role key stays off the browser and web host.
- `Supabase__ConnectionString`: optional PostgreSQL configuration, separate from the SQLite application connection. Storage does not require PostgreSQL access.
- `MonitoringApi__Devices__N__DeviceId`, `Kind` (`uno` or `camera`), `Credential`, `PairedDeviceId`: match the hardware's existing configuration. Device keys require at least 16 characters and are stored hashed in SQLite. Removed configured devices are disabled at startup. Configure both sides of each Uno/camera pair.

Matching web API keys were added to the local `.env` files. The cloned firmware contains only example configuration files; actual installed device credentials must be supplied to the server configuration. Do not substitute the public example placeholders. No firmware files are modified by this project.

## Web integration and deletion

Web account creation registers only the PIN. Registrations retry every 30 seconds. Deleting a member through ground control atomically removes the web account and queues monitoring cleanup in web SQLite. Delivery is attempted immediately and retried after outages. A successful server acceptance allows the web queue to clear; the server independently persists pending photo cleanup until Supabase succeeds.

Monitoring retires the PIN, revokes its device sessions, deletes its measurements/environment readings, and purges its entire PIN photo folder, including orphan objects. A PIN-only tombstone remains permanently reserved to prevent history being assigned to someone else. Web also reserves the keyed PIN lookup after account deletion. Retired development seed accounts are not recreated automatically. Keep the web PIN lookup secret and data-protection keys stable.

Photos use `{pin}/{measurementId}.jpg` within the configured bucket. Downloads stream through the server using its service key and work with private buckets. Photo uploads use short SQLite coordination leases so slow storage never holds a SQLite writer lock. Cleanup waits for in-flight uploads, including uploads whose local measurement was deleted, then removes late objects. Expired leases recover after process crashes. Incomplete remote uploads are reclaimed and orphan objects are removed without dropping the measurement. Retrying an interrupted upload uses the same object path.

The hardcoded web dashboard remains a template. It is not connected to live monitoring data or photos.

## API contract

Web calls use `X-Api-Key`. PINs are identifiers, not API authorization credentials. Device calls use their existing headers and device-bound sessions. Web access checks for crew/medic/ground-control remain in the web application.

| Method | Route | Behavior |
| --- | --- | --- |
| POST | `/api/v1/crew` | `{ "pin": "012345" }`; 201 new, 200 already active, 410 retired |
| GET | `/api/v1/crew/{pin}` | Active registration; 404 if missing/retired |
| DELETE | `/api/v1/crew/{pin}` | Idempotent retirement; 204 cleanup complete, 202 photo cleanup queued |
| POST / GET | `/api/v1/crew/{pin}/measurements` | Create / paginated history |
| GET | `/api/v1/crew/{pin}/measurements/{measurementId}` | One measurement |
| PUT / GET | `/api/v1/crew/{pin}/measurements/{measurementId}/photo` | Raw photo upload / private JPEG retrieval |
| POST / GET | `/api/v1/crew/{pin}/environment-readings` | Create / paginated history |
| GET | `/api/v1/healthz` | Public liveness check |

Both history endpoints accept `from`, `to` (UTC, maximum 31-day window), `limit` (1-100), and `cursor`. Responses use `{ "items": [...], "next_cursor": null }`. Keep the same time bounds when paging; pass the returned cursor unchanged and URL-encoded. Invalid values and cursors return structured errors.

Client reading/measurement IDs are UUIDs. New writes normalize their UUID format. The `(PIN, client ID)` database keys are unique: an identical retry returns the same record (200 on PIN routes), and a conflicting payload returns 409. JSON uses snake_case. Error responses consistently use `{ "error": { "code": "...", "message": "...", "request_id": "...", "details": [] } }`.

Photo uploads retain the existing JPEG/RGB565 formats, supported 160x120 and 320x240 dimensions, 5 MiB limit, normalization quality and five-minute upload window. A completed duplicate upload returns 200 if its input digest matches; different bytes return 409.

## Unchanged firmware compatibility

The server preserves `/api/v1/astronauts`, `/api/v1/auth/sessions`, `/api/v1/auth/sessions/refresh`, `/api/v1/auth/sessions/current` (DELETE), `/api/v1/measurements`, `/api/v1/measurements/{id}`, `/api/v1/measurements/{id}/photo`, and `/api/v1/environment-readings`.

The roster uses generated stable crew labels and transport handles, derived from the PIN and service secret; these are not stored usernames or additional identity keys. Login still accepts the firmware's `astronaut_id` and `pin` fields. Uno requests retain `X-Device-Id`, `X-Device-Key`, and `Authorization: Bearer`. Camera uploads retain their key-only header, resolved against the measurement's expected camera. Refresh requests require only the rotating refresh token. Token hashes and expiry times are stored in SQLite, and retirement invalidates them.

Firmware POST retries retain HTTP 201 because the current Uno requires that status. Existing payloads without `measured_at` receive a server UTC timestamp, reused for retries. `client_uptime_ms` remains accepted. No sensor sampling, camera capture, image dimensions, firmware settings or firmware source code are changed.

## Database upgrades and sample data

EF Core migrations now manage SQLite. Startup adopts the previous PIN-only `EnsureCreated` schema after validating it, then applies additive migrations without deleting readings. Unsupported older Identity-based monitoring databases require an explicit mapping rather than automatic destructive conversion. Back up SQLite before deployment upgrades. If existing rows violate new unique client-ID constraints, resolve those conflicts explicitly; migrations do not silently discard records.

In Development, run `dotnet run -- --seed-pin=123456` to create seven synthetic heart-rate measurements and seven environment readings. Repeated runs preserve existing rows and do not duplicate samples. Retired PINs cannot be seeded. No username is needed and no photos are uploaded.

## Verification

- `dotnet test Tests/MonitoringApi.Tests.csproj`
- `dotnet test ../ASTRA-Web/ASTRA-Web/Tests/Tests.csproj`

The server suite covers authentication, PIN registration/retirement, duplicate and concurrent writes, pagination, foreign keys, legacy-schema upgrades, .env precedence, private photos, storage outages, firmware requests, token revocation and deletion racing a slow upload. Web tests cover durable deletion, retry delivery, migration preservation and reserved PIN reuse. Tests use isolated temporary SQLite files and mocked storage; a separate live check also verified real Supabase upload, retrieval and deletion through ground control.
