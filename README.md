# InverterMonitor

C# Docker-first monitor for my solar inverter, currently targeting an SRNE /
Sun Gold Power inverter over a Waveshare RS232/485/422 TO POE ETH bridge.

The current implementation speaks raw Modbus RTU frames over TCP, matching the
Waveshare `Protocol = None`, `TCP Server`, port `4196` setup.

## Run Locally

```powershell
dotnet build --configfile .\NuGet.Config
$env:ASPNETCORE_URLS='http://localhost:5099'
.\bin\Debug\net10.0\InverterMonitor.exe
```

Open `http://localhost:5099`.

## Build Container

```powershell
docker build -t invertermonitor .
docker run --rm -p 8080:8080 invertermonitor
```

## Run With Docker Compose

Copy `.env.example` to `.env`, adjust the gateway/MQTT values, then run:

```powershell
docker compose up -d --build
```

Open `http://localhost:8080`.

The container reads initial settings from environment variables using the
`Monitor__...` names in `.env.example`. Settings changed in the web UI apply at
runtime, but are not persisted across container recreation yet.

## Dockhand Or Other Git Stack Docker Managers

For Docker managers that can deploy a Compose stack directly from Git, use:

- Repository: `https://github.com/GaryJS3/InverterMonitor`
- Stack name: `invertermonitor`
- Compose file path: `docker-compose.yml`
- Additional env file: leave blank unless your manager provides one

Set these deploy options:

- Build images on deploy: `ON`
- Re-pull images: `OFF`
- Force redeployment: `OFF`

`Build images on deploy` is required because this repository currently builds
the container image from the included `Dockerfile`. `Re-pull images` is only
needed if the stack is changed later to use a prebuilt registry image such as
`ghcr.io/...:latest`. `Force redeployment` is optional and normally not needed
unless the stack should restart even when Git has no changes.

Add the environment variables from `.env.example` in the manager UI, or provide
an env file through the manager if supported.

## Initial Known Settings

- Gateway: `10.44.0.173`
- Port: `4196`
- Slave: `1`
- Serial bridge mode: raw TCP / transparent / `Protocol = None`
- Serial: `9600 8N1`

MQTT publishing and Home Assistant discovery are wired. Discovery publishes
retained config topics under `homeassistant/sensor/.../config` when enabled.

## Inverter Definitions

Each inverter model lives in `InverterDefinitions/*.json`. A definition includes
brand/model metadata, protocol defaults, read behavior, and register entries.
Register addresses may be decimal strings or hex strings such as `0x021B`.
