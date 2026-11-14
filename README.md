# ASTRA-Monitoring
The monitoring project for the server

This is a separate ASP.NET Core host for the draft `/api/v1` monitoring contract. It shares the web app's Identity SQLite database for astronaut accounts and PIN verification. It stores device registrations, sessions, measurements, telemetry, and photo files in a separate SQLite database in this project directory (`monitoring.db`) by default. The default Identity connection points to the sibling ASTRA-Web checkout; start ASTRA-Web once to initialize that database, or set `ConnectionStrings:DefaultConnection` to the shared Identity database path. Override `ConnectionStrings:DefaultConnection` and `MonitoringApi:ConnectionString` for deployment paths.

Run it from this directory with `dotnet run` (or `dotnet run --project ASTRA-Monitoring.csproj`). Bind it behind HTTPS and retain normal server certificate validation on the Uno and camera. The API does not seed device credentials. Before starting it, provision unique credentials in user secrets or the deployment secret store:

To create a local sample dataset in an empty monitoring database, run `dotnet run -- --seed-demo` instead. This creates two demo Crew accounts, paired demo devices, 10 synthetic measurements, 20 synthetic ambient readings, and expired photo records. It writes the one-time PINs and device keys to `.demo-credentials.json` with owner-only file permissions on Unix; that file is gitignored. This command refuses to run outside Development or when the monitoring database/demo usernames already contain data. Start the server normally afterward with `dotnet run`.

```sh
dotnet user-secrets set 'MonitoringApi:Devices:0:DeviceId' 'uno-r4-crew-03' --project ASTRA-Monitoring.csproj
dotnet user-secrets set 'MonitoringApi:Devices:0:Kind' 'uno' --project ASTRA-Monitoring.csproj
dotnet user-secrets set 'MonitoringApi:Devices:0:Credential' '<unique-random-secret>' --project ASTRA-Monitoring.csproj
dotnet user-secrets set 'MonitoringApi:Devices:0:PairedDeviceId' 'cam-crew-03' --project ASTRA-Monitoring.csproj
dotnet user-secrets set 'MonitoringApi:Devices:0:AstronautIds:0' '<Identity-user-id>' --project ASTRA-Monitoring.csproj
dotnet user-secrets set 'MonitoringApi:Devices:1:DeviceId' 'cam-crew-03' --project ASTRA-Monitoring.csproj
dotnet user-secrets set 'MonitoringApi:Devices:1:Kind' 'camera' --project ASTRA-Monitoring.csproj
dotnet user-secrets set 'MonitoringApi:Devices:1:Credential' '<different-unique-random-secret>' --project ASTRA-Monitoring.csproj
dotnet user-secrets set 'MonitoringApi:Devices:1:PairedDeviceId' 'uno-r4-crew-03' --project ASTRA-Monitoring.csproj
```

Use `X-Device-Id` and `X-Device-Key` over HTTPS for device-authenticated calls. Secrets are hashed in the API database and re-provisioned from the secret store at startup. Roster assignments are scoped to each Uno. Astronaut IDs are Identity user IDs; users need the `Crew` or `Medic` role and must already be provisioned by the web app. Astronaut-scoped calls use opaque, rotating bearer/refresh tokens. Medics and ground control can read all measurement history through the shared `ReadMedicalRecord` policy; crew can read their own.

Photo uploads are raw request bodies, limited to 5 MiB, and stored outside the web root with opaque filenames. The API validates JPEG dimensions and exact RGB565 byte length, then normalizes either format to JPEG for retrieval. RGB565 input uses the provisioned big-endian byte order. Photo retrieval stays authenticated and uses `Cache-Control: private, no-store`. Do not send PINs, tokens, or photos over the camera's local capture-control HTTP interface.

This is a demo implementation of the draft contract and is not a certified medical or emergency-response interface. Device certificate authentication, audit/retention policy, production rate limiting, operational privacy/security review remain deployment work.

Launch with -  dotnet run --urls "http://0.0.0.0:5000;https://0.0.0.0:7191"
