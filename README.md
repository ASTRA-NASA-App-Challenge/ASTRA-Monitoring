# ASTRA Monitoring

Standalone SQLite API. Only six-digit string PINs are stored in CrewMembers. Measurement and environment rows reference the PIN through foreign keys. Web accounts remain separate; no synchronization is enabled.

POST /api/v1/crew with {"pin":"012345"} registers a PIN (201 created, 409 duplicate). GET /api/v1/crew/{pin} checks it. Under /api/v1/crew/{pin}, POST/GET measurements creates/lists measurements; GET measurements/{id} retrieves one; PUT/GET measurements/{id}/photo uploads/retrieves a photo; POST/GET environment-readings creates/lists telemetry. Unknown PINs are rejected. Photos use bucket paths {pin}/{measurementId}.jpg.

Run dotnet run. Both hosts load .env from their project directory. Existing configuration and user secrets override .env; environment variables and command-line options take highest priority. .env.example files document settings; .env files are ignored by Git. Fill in Supabase password and service-key placeholders locally. The web host configuration is in ASTRA-Web/ASTRA-Web/Web.

ConnectionStrings__DefaultConnection is always SQLite. Put the supplied PostgreSQL connection string in Supabase__ConnectionString instead. Storage uses Supabase__Url, Supabase__Bucket and Supabase__ServiceRoleKey. The web uses only the publishable key. Do not clear user secrets: correct an old PostgreSQL DefaultConnection override instead. Preserve the web Identity__PinLookupKey for existing accounts; an existing user secret overrides the generated local value.

Configure cameras with MonitoringApi__Devices__0__DeviceId, MonitoringApi__Devices__0__Kind=camera and MonitoringApi__Devices__0__PairedDeviceId (Uno ID). Devices are provisioned at startup.

Use a fresh SQLite file for this schema. Existing monitoring databases need an explicit migration and old-account-to-PIN mapping; EnsureCreated does not upgrade them. Back up existing data and select a new filename before starting. Legacy Identity/session/demo sources are excluded from compilation. No remote database changes were made.

PIN routes identify data and do not provide account authentication. Photo retrieval streams through the server using its Supabase service key and supports private buckets. JPEG/RGB565 validation and the 5 MiB upload limit remain.

