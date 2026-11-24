# Deploying the web HMI

Each release has one zip per platform (`win-x64`, `linux-x64`, `linux-arm64`).
Each is self-contained: no .NET install is needed on the bench PC.

| File | What it is |
|------|------------|
| `MotorBench.Web` / `MotorBench.Web.exe` | the HMI |
| `appsettings.json` | settings and users, see [web-hmi.md](web-hmi.md) |
| `wwwroot/`, `profiles/` | the pages and the test profiles |
| `bench_control.st` | the PLC program this release talks to |
| `motorbench.service` | systemd unit |

The HMI reads `appsettings.json` and `wwwroot/` from the folder it is started
in, and puts `data/` there unless `DataDir` says otherwise. The service setups
below handle this. When you start it by hand, `cd` into its folder first.

## Linux (systemd)

```sh
sudo unzip motorbench-1.2.0-linux-x64.zip -d /opt          # unpacks into /opt/motorbench
sudo cp /opt/motorbench/motorbench.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now motorbench
journalctl -u motorbench -f                                # logs
```

The unit runs the HMI as an unprivileged throwaway user. Run data goes to
`/var/lib/motorbench`. To use a real PLC, add
`Environment=Plc__Host=192.168.0.10` to the unit (or set `Plc:Host` in
`appsettings.json`).

## Windows (service)

In an administrator PowerShell, after unpacking to `C:\MotorBench`:

```powershell
mkdir C:\ProgramData\MotorBench
icacls C:\ProgramData\MotorBench /grant "LOCAL SERVICE:(OI)(CI)M"
sc.exe create MotorBench obj= "NT AUTHORITY\LocalService" start= delayed-auto `
  binPath= "C:\MotorBench\MotorBench.Web.exe --DataDir C:\ProgramData\MotorBench"
sc.exe failure MotorBench reset= 86400 actions= restart/5000
sc.exe start MotorBench
```

Warnings and errors go to the Windows Event Log (Application log).

## Stopping

Stopping the service (`systemctl stop`, `sc.exe stop`, or Ctrl+C) aborts any
running sequence and sends Stop to the PLC. It keeps the PLC watchdog alive
until the motor reaches standstill (10 s at most), so the bench ends in Idle,
not in a comm-loss fault.

## Monitoring

No login is needed for these, and they give no details beyond the status:

| URL | 200 when | Otherwise |
|-----|----------|-----------|
| `/healthz` | the process is up | no answer |
| `/readyz` | the PLC is connected, its program is scanning, and its register map matches | 503, reason in the log |

`/api/version` (logged in) and the page footer show the HMI version and the
PLC register map version it expects.

## Upgrading

1. Read the release notes. If the **register map version** changed, load the
   new `bench_control.st` into the PLC first. Until you do, the HMI stays
   disconnected and shows "PLC program implements register map version N"; it
   never writes to a program it does not know.
2. Stop the service and replace everything except `appsettings.json`.
3. Start the service and check `/readyz`.

## Making a release

```sh
git tag v1.2.0 && git push origin v1.2.0
```

`.github/workflows/release.yml` runs the tests, builds the three zips with
that version stamped in, and attaches them to a GitHub release.
