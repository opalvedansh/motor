# Web HMI: configuration and security

`src/MotorBench.Web` is the browser HMI. Settings come from
`src/MotorBench.Web/appsettings.json`, environment variables (`Plc__Host=...`) or
the command line (`--Plc:Host ...`), later sources winning.

## Settings

| Key | Default | Meaning |
|-----|---------|---------|
| `Plc:Host` | empty | PLC address. Empty runs the built-in simulator. Alias `--plc-host`. |
| `Plc:Port` | 502 | Modbus TCP port. Alias `--plc-port`. |
| `Plc:UnitId` | 1 | Modbus unit id. |
| `Plc:PollIntervalMs` | 100 | Status poll + watchdog period (50–1000). |
| `Plc:TimeoutMs` | 1000 | Reply timeout per Modbus request. |
| `Plc:CommandTimeoutMs` | 500 | Time the PLC may take to accept start/stop/reset. |
| `DataDir` | `data` | Step reports, alarm history, audit log. Alias `--data-dir`. |
| `DataRetentionDays` | 365 | Run files older than this are deleted (at startup and daily). 0 keeps them forever. Audit and alarm logs are never deleted. |
| `urls` | `http://localhost:5080` | Where the HMI listens. |
| `AllowedHosts` | `localhost;127.0.0.1;[::1]` | Host names the HMI answers to (DNS-rebinding guard). |
| `Users` | none | Accounts, see below. |

Invalid values (port 70000, a malformed password hash, a duplicate user) stop
the app at startup with a message naming the setting.

## Local mode and logins

- **No users configured**: local mode. The HMI only listens on this machine and
  everyone using it is the `local` engineer. This is the one-command demo.
- **Users configured**: everyone logs in. Sessions last one shift (12 h, sliding).
  Login attempts are limited to 5 per minute per address.

| Role | Can |
|------|-----|
| `Viewer` | watch status, trends, results; download reports |
| `Operator` | + take control, start/stop/reset, setpoints, run/hold sequences |
| `Engineer` | + fault injection (simulated E-stop, runaway drive) |

Create a password hash (the password is read from the terminal, never from the
command line, so it does not end up in shell history):

```
dotnet run --project src/MotorBench.Web -- hash-password
```

```json
"Users": [
  { "Name": "tanaka", "PasswordHash": "pbkdf2-sha256$600000$...", "Role": "Operator" },
  { "Name": "sato",   "PasswordHash": "pbkdf2-sha256$600000$...", "Role": "Engineer" }
]
```

Removing a user or changing their role takes effect on the next request after a
restart; existing sessions are rejected.

## Reaching the HMI from other PCs

The HMI refuses to start if `urls` contains a non-loopback address unless all
URLs are HTTPS and at least one user is configured:

```json
"urls": "https://0.0.0.0:5443",
"AllowedHosts": "bench-pc;bench-pc.factory.local",
"Kestrel": { "Certificates": { "Default": { "Path": "bench-pc.pfx", "Password": "..." } } }
```

## Control lock

Only one browser tab operates the bench at a time; other tabs are view-only and
show who has control. The page takes control automatically when nobody has it.
The lock is released with **Release control**, on logout, or 5 s after the
holder's page stops polling (closed tab, lost network).

**Stop and Abort never need the lock.** Any Operator or Engineer can stop the
bench from any tab.

## Run records (traceability)

Every test run needs a **DUT serial** (up to 40 letters, digits, `.`, `_`, `-`).
Each run that starts leaves two files in `DataDir`, aborted runs included:

| File | Content |
|------|---------|
| `result_<profile>_<finished>.csv` | step results, with a header recording the outcome, DUT serial, operator, start time, the SHA-256 of the exact profile file that ran, and the raw data file name |
| `raw_<profile>_<serial>_<started>.csv` | one row per poll (100 ms) for the whole run |

```
# profile,quick_check
# finished,2026-11-23 21:55:08
# outcome,completed
# dut_serial,M-2026-0042
# operator,local
# profile_sha256,525e1c0b...
# started,2026-11-23 21:54:50
# raw_data,raw_quick_check_M-2026-0042_20261123_215450.csv
step,speed_sp_rpm,load_pct,dwell_s,settle_s,avg_speed_rpm,avg_torque_nm,max_temp_c
```

A run refused before it starts (e.g. alarm not reset) leaves no files. If the raw
data file cannot be created the run does not start; if writing fails mid-run the
run is aborted and the bench stopped. The **Run history** panel lists the last
100 runs with downloads (`GET /api/runs`). Only run files can be downloaded,
not the audit or alarm logs.

## Audit log

`DataDir/audit.csv` gets one row per API action, including refused ones:
time, user, action, request body (first 512 bytes), HTTP result. Logins are
recorded with the user name only, never the password. Values are escaped so
the file is safe to open in Excel.

## Calling the API directly

Every request must carry `X-MotorBench-Client: <any id up to 64 chars>`. It
identifies the tab for the control lock, and POSTs without it are refused
(cross-site request guard).

```
H='X-MotorBench-Client: script-1'
curl -H "$H" -X POST localhost:5080/api/control/take
curl -H "$H" -H 'Content-Type: application/json' -d '{"profile":"quick_check","dutSerial":"M-0042"}' localhost:5080/api/sequence/start
curl -H "$H" localhost:5080/api/status
```

A script holding control must keep polling `/api/status` at least every 5 s.

For running it as a service, health checks and upgrades, see [deploy.md](deploy.md).
