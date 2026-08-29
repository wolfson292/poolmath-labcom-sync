# poolmath-labcom-sync

Syncs water test results from a PoolLab photometer (via the LabCOM Cloud) into
[Pool Math](https://troublefreepool.com/), for three bodies of water. Runs as a single
container on a NUC.

## How it works

Every 15 minutes the service reads the LabCOM cloud account, groups new measurements into test
sessions, and writes one Pool Math test log per session.

```
LabCOM Cloud (GraphQL)  ──►  group into sessions  ──►  map to Pool Math fields  ──►  POST /testlogs
                                                                                          │
                                        /data/state.json  ◄── high-water mark ────────────┘
```

A PoolLab records one measurement per parameter, a few minutes apart. Readings from the same water
body that chain together within `SessionWindow` (20 min) become a single test log, so a run that
measures pH, FC and TA produces one Pool Math entry rather than three. A session is only written
once its newest reading is `SessionSettleTime` (10 min) old, so a test still in progress isn't
split across two entries.

Duplicates are prevented by a per-water-body high-water mark on the LabCOM measurement id, stored
in `/data/state.json`. A failed run writes nothing and retries the same readings on the next tick.

## A caveat worth knowing

**Pool Math has no public API.** The only documented endpoint is the read-only share URL
(`api.poolmathapp.com/share/tfp-XXXXXX.json`). To write, this service signs in with a Trouble Free
Pool account and uses the same private API the official apps use.

That API is undocumented and unsupported, so TFP can change it without notice. The request shape is
pinned by tests in `tests/PoolSync.Tests`; if writes start failing after a Pool Math update, capture
a current request from the official web client and compare it against those assertions.

LabCOM, by contrast, is a supported public API — GraphQL at `backend.labcom.cloud`, with a token
you generate yourself.

## Setup

### 1. Get the two credentials

- **LabCOM token** — https://labcom.cloud/pages/user-setting
- **Pool Math** — your Trouble Free Pool forum **username** (not your email) and password

```bash
cp .env.example .env
```

Fill in `POOLSYNC_LabCom__ApiToken`, `POOLSYNC_PoolMath__Username` and
`POOLSYNC_PoolMath__Password`.

### 2. Avoid storing the password (optional but recommended)

Exchange the password for a long-lived token once, then keep only the token on the NUC:

```bash
dotnet run --project src/PoolSync -- print-token
```

Paste the two lines it prints into `.env` and clear `Username`/`Password`. The authorization shows
up in your Pool Math account under the device name `Mobile App (LabCOM Sync)`, so you can revoke
just this one later.

### 3. Map the water bodies

List both sets of ids:

```bash
docker compose run --rm poolsync list-pools
docker compose run --rm poolsync list-accounts
```

Pair them up in `.env`, one index per water body:

```
POOLSYNC_WaterBodies__0__Name=Pool
POOLSYNC_WaterBodies__0__LabComAccountId=<from list-accounts>
POOLSYNC_WaterBodies__0__PoolMathPoolId=<from list-pools>
```

Indexes 1 and 2 cover the other two. Three slots are defined in
[appsettings.json](src/PoolSync/appsettings.json); add more by adding higher indexes.

### 4. Dry run first

`DryRun=true` is the default. Start it and read the logs: every test log it *would* write is
printed as JSON.

```bash
docker compose up -d
docker compose logs -f
```

Check the values and timestamps against what the LabCOM app shows. When it looks right, set
`POOLSYNC_Sync__DryRun=false` in `.env` and `docker compose up -d` again.

> On the first real run the service imports the last 7 days (`InitialBackfill`). Shorten it to
> `1.00:00:00` first if you'd rather start small.

## Deploying to the NUC

Building on an Apple Silicon Mac targets arm64 by default; the NUC needs amd64:

```bash
docker buildx build --platform linux/amd64 -t poolmath-labcom-sync:latest --load .
```

Or just build on the NUC itself with `docker compose up -d --build`.

## Endpoints

| Path      | Purpose                                                                    |
| --------- | -------------------------------------------------------------------------- |
| `/health` | 200 while healthy, 503 after 3 consecutive failed runs. Used by the container healthcheck. |
| `/status` | Last run, last error, and per-water-body detail as JSON.                    |

## Configuration

Everything is settable as `POOLSYNC_Section__Key` environment variables. Note that
[docker-compose.yml](docker-compose.yml) passes an explicit list of these through to the container —
to override a setting it doesn't already list, add it there too.

| Setting                    | Default        | Notes                                            |
| -------------------------- | -------------- | ------------------------------------------------ |
| `Sync:Interval`            | `00:15:00`     | How often LabCOM is polled.                      |
| `Sync:SessionWindow`       | `00:20:00`     | Max gap between readings in one test session.    |
| `Sync:SessionSettleTime`   | `00:10:00`     | How long a session must be idle before writing.  |
| `Sync:InitialBackfill`     | `7.00:00:00`   | How far back the first run imports.              |
| `Sync:DryRun`              | `true`         | Log what would be written, write nothing.        |
| `Sync:StatePath`           | `/data/state.json` | Must be on the mounted volume.               |
| `Mapping:DeriveCombinedChlorine` | `true`   | CC = total chlorine − free chlorine.             |
| `Mapping:WaterTempUnits`   | `0`            | 0 = Fahrenheit, 1 = Celsius.                     |
| `Mapping:NoteTemplate`     | *(empty)*      | Set e.g. `Imported from PoolLab {device}` to tag logs. |

### Parameter mapping

LabCOM parameters are matched on scenario id first, then on parameter name — see
[MappingOptions.cs](src/PoolSync/Configuration/MappingOptions.cs). Covered by default: pH, free and
total chlorine, alkalinity, CYA, calcium hardness, salt, borate, TDS and water temperature.

Anything unmapped is skipped and logged at debug level. To add one, set
`POOLSYNC_Mapping__ByParameter__<LabCOM parameter name>=<pool math field>`, where the field is one
of `ph`, `fc`, `cc`, `ta`, `cya`, `ch`, `salt`, `bor`, `tds`, `waterTemp`.

## Development

```bash
dotnet test
dotnet run --project src/PoolSync -- list-accounts
```

Set `Sync:StatePath` to a local path when running outside Docker.

## Troubleshooting

**"No LabCOM account \<id\> for water body"** — the log lists every account id the token can see.
Copy the right one into `.env`.

**Sign-in returns 401** — `PoolMath:Username` is the TFP forum username, not the email address.

**Readings split across two Pool Math entries** — raise `Sync:SessionWindow`.

**A session was written with missing parameters** — raise `Sync:SessionSettleTime` so slower runs
finish before the session is pushed.

**Re-import after a mistake** — stop the container, edit or delete `/data/state.json`, restart.
Lowering `LastMeasurementId` re-imports everything above it, which will create duplicate Pool Math
entries; delete those in the app.
