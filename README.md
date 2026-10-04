# poolmath-labcom-sync

Keeps the water test history for three bodies of water, imports PoolLab photometer results from the
LabCOM Cloud, and works out CSI and what to add. Started as a LabCOM → [Pool Math](https://troublefreepool.com/)
sync; it now keeps its own history so Pool Math is optional. Runs as a single container on a NUC.

## How it works

Every 15 minutes the service reads the LabCOM cloud account, groups new measurements into test
sessions, and stores one test per session in its own database — and, if `PoolMath:WriteLogs` is on,
copies it to Pool Math too.

```
LabCOM Cloud (GraphQL)  ──►  group into sessions  ──►  /data/poolsync.db  ──►  (optional) Pool Math
                                     │
           /data/state.json  ◄── high-water mark
```

A PoolLab records one measurement per parameter, a few minutes apart. Readings from the same water
body that chain together within `SessionWindow` (20 min) become a single test log, so a run that
measures pH, FC and TA produces one Pool Math entry rather than three. A session is only written
once its newest reading is `SessionSettleTime` (10 min) old, so a test still in progress isn't
split across two entries. **Sync now** skips that wait: press it once the last parameter is
measured and the test is written straight away.

Duplicates are prevented by a per-water-body high-water mark on the LabCOM measurement id, stored
in `/data/state.json`. A failed run writes nothing and retries the same readings on the next tick.

## Local history, and leaving Pool Math

Every test lives in `/data/poolsync.db` (SQLite): LabCOM sessions, tests typed in on the page, and
anything imported from Pool Math. The current readings, CSI and dosing all come from there, never
from Pool Math. Each pool's settings (volume, surface, salt cell, salt and borate targets, FC
override, temperature unit) are kept there too, edited under **Pool settings** on its card. They
are copied from Pool Math once, the first time the service sees the pool.

To move off Pool Math:

1. Press **Import Pool Math history** at the bottom of the page (or `POST /import/poolmath`). It
   copies every test, chemical addition and maintenance entry on the account. Run it as often as you
   like; entries already imported are skipped.
2. Check each pool's settings and history on the page.
3. Cancel the subscription and set `PoolMath:Enabled=false`. Nothing else changes.

Imported chemical additions keep Pool Math's own chemical and unit codes; the units seen so far
(fl oz, gal, oz) are decoded, the rest are kept as codes. Imported tests keep their weather.

Tests are trusted; live sensors aren't. Readings from pool controllers can drift, so they are never
mixed into the test history — they are compared against it instead.

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

A dry run is side-effect free: it does not advance the high-water mark, so every session it reports
is still written once you go live, and repeated dry runs keep showing the same output.

> On the first real run the service imports the last 7 days (`InitialBackfill`). Shorten it to
> `1.00:00:00` first if you'd rather start small.

## Deploying

The image is built for `linux/amd64` by [GitHub Actions](.github/workflows/build-image.yml) on every
push to `main` and published to `ghcr.io/<owner>/poolmath-labcom-sync:latest`.
[docker-compose.yml](docker-compose.yml) pulls that image, so the target host needs no .NET SDK and
no build step. Point `POOLSYNC_IMAGE` elsewhere to use a different registry or tag.

As a Portainer stack, add it as a Git-backed stack pointing at this repo with
`docker-compose.yml` as the compose path, and set the variables from `.env.example` as stack
environment variables.

> Deploying with a `build:` section through a Portainer *agent* fails on some setups — the agent
> can't reach BuildKit (`failed to list workers`). Pulling a prebuilt image avoids that entirely.

To build locally instead of pulling:

```bash
docker compose -f docker-compose.yml -f docker-compose.build.yml up -d --build
```

## Water balance and dosing

Each water body's card on the status page shows its CSI and what to add to bring the water to the
levels Pool Math recommends, with amounts for the pool's volume.

- **Readings** — the newest value of each parameter wins, wherever it came from: the latest LabCOM
  test, Pool Math's running summary (CH and salt usually come from an older entry there), or a value
  typed in on the page. Each value shows its source and date when it isn't from the latest test.
- **Tests by hand** — **Add a test** on each card takes any reading (FC, CC, pH, TA, CYA, CH, salt,
  borate, temperature), at the current time or a past one, with a note. A PoolLab doesn't measure
  CH, salt, borate or temperature, so those usually come from here. Hand-entered tests can be
  deleted from **History**; synced and imported ones can't, since they would only come back.
- **Implausible values** — a stored value outside what any test could read (an FC of 1,000,000, a
  pH of 12) is skipped in favour of the last good one, and hand entry refuses it.
- **CSI** — Trouble Free Pool's published formula, which is what Pool Math uses, including the CYA
  and borate corrections to alkalinity. Tests pin it against CSI values Pool Math reported for real
  pools. The card also shows what CSI would be after the recommended changes.
- **Targets** — TFP's recommended levels: pH 7.6–7.8, TA 60–80, CH by surface (350–550 for
  plaster/fiberglass, 50–550 for vinyl), CYA 40–50 (70–80 with a salt cell), FC from CYA (12.5% of
  CYA, 7.5% with a salt cell). The salt and borate targets and any FC override in the pool's
  settings take precedence, and salt and borate are only checked where the pool has a target.
- **Doses** — liquid chlorine, muriatic acid or soda ash for pH, baking soda, calcium chloride,
  stabilizer, salt and boric acid. The pH dose comes from a carbonate model that accounts for TA,
  CYA and borate buffering, so 50 ppm of borate needs several times the acid. Parameters that can
  only come down by dilution get a percentage of water to replace. Like Pool Math's, these are
  estimates: add part, circulate, retest. Stabilizer is given in cups as well as weight, at about
  2¼ cups to the pound.
- **Mislabelled temperatures** — a Pool Math temperature saved as °C but above 45 is read as °F
  (81.9 °C is not pool water; 81.9 °F is), and the card says so until the entry is corrected.

Surface, salt cell and the targets are set per pool under **Pool settings** on its card. Product
strengths are `Balance:ChlorinePercent` (default 10) and `Balance:AcidPercent` (default 31.45; use
14.5 for half-strength).

## Logs, reminders and pool controllers

**Chemicals and maintenance.** Each card can log a chemical added (liquid chlorine, acid, baking
soda, soda ash, calcium chloride, stabilizer — by weight or by the cup — salt, boric acid, borax,
cal-hypo, dichlor, trichlor) and maintenance (backwash, brush, vacuum, filter clean, salt cell
clean, filter pressure). While you type an amount, the form previews the effect on the current
readings — FC, CYA, CH, salt and borate directly, and pH and TA through the same carbonate model as
the doses — and on CSI. Imported Pool Math chemical entries keep Pool Math's numeric codes.

**Reminders.** Each maintenance task can have an interval under **Pool settings** (brush and vacuum
7 days, filter clean 90, and salt cell clean 90 on salt pools by default). A task is due that long
after it was last logged; one never logged isn't nagged about.

**Pool controllers.** Set `WaterBodies:N:Controller:HomeAssistant` (instance index) and
`Controller:Device` (the ESPHome device name in entity ids, e.g. `pool_antenna`), and the service
finds the controller's sensors in Home Assistant by name: pH probe, ORP, water temperature (a spa's
heater counts), salt cell salt and output, filter pressure, pump speed and power, and the acid
doser's counters. Every sync it:

- **samples** each sensor into its own table — never into the test history, since probes drift;
- **compares** each test from the last 30 days with what the controller read at that moment
  (averaged over ±10 minutes of HA history) and shows the difference, warning past the pool's limit
  (pH 0.2, salt 400 ppm, 1.5 °C by default);
- **logs acid doses** from the doser's "dosed today" counter as additions, and tank refills as
  maintenance — read each run, since HA doesn't keep these counters' history;
- **checks the equipment**: active fault flags (salt cell alarms, dose faults), the acid tank below
  its low mark, filter pressure 8 psi above clean at the same pump speed, and a pump drawing 25% more
  or less power than usual for its speed.

**FC between tests.** Where a controller has an ORP probe, each FC test is paired with the ORP at
that moment, and ORP is fitted against log(FC / CYA) for that pool over the last 90 days. Once
there are at least three such tests spread across different FC levels and the fit holds (R² ≥ 0.5),
the card shows FC estimated from live ORP, its trend over the last 6 hours, and when it will reach
the minimum, with a warning if that's within 6 hours or already past. Until then it says how many
more tests it needs.

**Rain dilution.** Each pool has a location (filled in from its controller's Home Assistant) and a
surface area (from the controller, or estimated from volume; a spa is assumed covered). Daily rain
and evaporation come from Open-Meteo, which needs no key and goes back decades. Following the level
from each CYA, CH, salt and borate test — rain up, evaporation down, overflow above normal carrying
chemicals out, a 2 in drop assumed topped up with fresh water — gives how much has overflowed and
what the reading has likely been diluted to. It's shown beside the reading as an estimate; tests
stay the record and drive the dosing.

**Trends.** Each card charts every test reading over 30 days, 90 days, a year, or all of it, with
the ideal range shaded and low/average/high in the title, plus the controller's sensors as hourly
means on their own charts.

**Alerts and entities.** Warnings and overdue reminders go to Home Assistant instance 0's notify
service (`notify.all_devices`), once, then daily while they last. Each water body's CSI (with the
recommendations as attributes) and current readings are published as `sensor.poolsync_<pool>_*`
entities, refreshed every sync.

## Endpoints

| Path      | Purpose                                                                    |
| --------- | -------------------------------------------------------------------------- |
| `/`       | Status page: the latest readings for each water body and a **Sync now** button. |
| `/health` | 200 while healthy, 503 after 3 consecutive failed runs. Used by the container healthcheck. |
| `/status` | Last run, last error, and per-water-body readings as JSON.                  |
| `POST /tests` | Saves a hand-entered test: `{"waterBody": "Pool", "takenAt": null, "ph": 7.6, "waterTemp": 84, "waterTempUnits": 0, "ch": 350, "notes": "Taylor"}` (any reading may be omitted; `takenAt` null = now; units 0 = °F, 1 = °C). 400 if a value is implausible. |
| `GET /tests?waterBody=Pool&limit=50` | A water body's test history, newest first. |
| `DELETE /tests/{id}` | Deletes a hand-entered test. 404 for synced or imported ones. |
| `POST /import/poolmath` | Imports the Pool Math account's whole history; repeatable. |
| `PUT /settings/{waterBody}` | Replaces a water body's pool settings. |
| `GET/POST /additions`, `DELETE /additions/{id}` | Chemical additions; only hand-entered ones can be deleted. |
| `POST /effects` | Previews an addition's effect on the current readings; saves nothing. |
| `GET/POST /maintenance`, `DELETE /maintenance/{id}` | Maintenance entries. |
| `GET /chemicals` | The products and units the forms offer. |
| `GET /trends?waterBody=Pool&days=90` | Tests in the range, and controller samples as hourly means. |
| `POST /sync` | Runs a sync immediately, writing sessions without waiting out `SessionSettleTime`. 200 with the number of logs written, 409 if a run is already in progress, 502 if the run failed. |

The readings shown are the newest LabCOM holds, which is not always what has been synced — a water
body whose last test predates the backfill window still shows its readings, with the test date
alongside so a stale one is obvious.

Each pool's name links to its public Pool Math share page when one exists. The share code is read
from Pool Math on every run rather than configured, so a pool is linked only while sharing is
actually enabled for it and the link appears on its own once you turn sharing on in the app.

> These endpoints are unauthenticated, including `POST /sync`. That's fine on a trusted LAN; don't
> publish the port to the internet directly — put it behind a reverse proxy that authenticates
> (see [Reverse proxy](#reverse-proxy)). `POST /sync` only ever triggers the same work the timer does,
> and concurrent runs are rejected rather than queued, so it can't be used to double-write.

## Reverse proxy

[docker-compose.yml](docker-compose.yml) also joins the container to the reverse proxy's Docker
network (`home` by default; set `POOLSYNC_PROXY_NETWORK` to change it), so the proxy can reach it as
`poolmath-labcom-sync:8080`. The status page uses relative URLs, so it works under a subfolder as
long as the proxy strips the prefix. For SWAG, at `nginx/proxy-confs/poolsync.subfolder.conf`:

```nginx
location /poolsync {
    return 301 $scheme://$host/poolsync/;
}

location ^~ /poolsync/ {
    include /config/nginx/proxy.conf;
    include /config/nginx/authelia-location.conf;
    include /config/nginx/resolver.conf;
    set $upstream_app poolmath-labcom-sync;
    set $upstream_port 8080;
    set $upstream_proto http;
    rewrite ^/poolsync/(.*)$ /$1 break;
    proxy_pass $upstream_proto://$upstream_app:$upstream_port;
}
```

Keep the authentication include: without it anyone who finds the URL can trigger syncs.

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
| `Sync:DatabasePath`        | `/data/poolsync.db` | Test history and pool settings; on the mounted volume. |
| `PoolMath:Enabled`         | `true`         | Contact Pool Math at all (import, share links, settings seed). |
| `PoolMath:WriteLogs`       | `false`        | Also copy each new test to Pool Math.            |
| `Mapping:DeriveCombinedChlorine` | `true`   | CC = total chlorine − free chlorine.             |
| `Mapping:WaterTempUnits`   | `0`            | 0 = Fahrenheit, 1 = Celsius.                     |
| `Mapping:NoteTemplate`     | *(empty)*      | Set e.g. `Imported from PoolLab {device}` to tag logs. |
| `Balance:ChlorinePercent`  | `10`           | Liquid chlorine strength for FC doses.           |
| `Balance:AcidPercent`      | `31.45`        | Muriatic acid strength for pH doses.             |
| `WaterBodies:N:Surface`    | `Plaster`      | Initial surface for a new pool's settings; edit it on the page after. |
| `WaterBodies:N:Swg`        | *(from Pool Math)* | Initial salt-cell setting for a new pool's settings. |

### Parameter mapping

LabCOM parameters are matched on scenario id first, then on parameter name — see
[MappingOptions.cs](src/PoolSync/Configuration/MappingOptions.cs). Covered by default: pH, free and
total chlorine, alkalinity, CYA, calcium hardness, salt, borate, TDS and water temperature.

Readings outside `Mapping:ValidRanges` are dropped with a warning instead of written. A PoolLab
reports an over-range result as a number off the end of its scale — an FC of 1,000,000, a pH of
9.0 — and Pool Math would take that as the pool's current value. The defaults are the PoolLab 1.0
measuring ranges (pH 6.5–8.4, FC/TC 0–8, TA 0–200, CYA 0–160, CH 0–500); widen one with e.g.
`POOLSYNC_Mapping__ValidRanges__fc__Max=10`. A value LabCOM formats as a bound (`>8.4`) is dropped
too.

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

**Re-import after a mistake** — without host shell access, point `Sync:StatePath` at a new filename
and redeploy; the service finds no state there and restarts from the backfill window. With shell
access, stop the container, edit or delete `/data/state.json`, restart.
Lowering `LastMeasurementId` re-imports everything above it, which will create duplicate Pool Math
entries; delete those in the app.
