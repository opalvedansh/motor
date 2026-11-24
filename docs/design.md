# Design notes

## Overview

```
 +---------------------------+        Modbus TCP :502         +------------------------------+
 |  MotorBench.App (PC/HMI)  | <----------------------------> |  OpenPLC Runtime             |
 |  WinForms, C#             |   FC01 coils, FC03/06/16 HR    |  bench_control.st            |
 |                           |                                |   - interlocks / state       |
 |  - manual operation       |                                |   - alarms (first-out)       |
 |  - auto test sequences    |                                |   - BenchSim FB (stands in   |
 |  - trend, logging         |                                |     for drive + dyno + I/O)  |
 +---------------------------+                                +------------------------------+

 MotorBench.Sim implements the same register map in C# so the HMI can be
 developed and tested without OpenPLC running.
```

The PLC owns everything safety-related: start permission, stopping on alarms,
the watchdog. The PC only sends requests and setpoints. If the PC crashes or
the cable is pulled, the PLC stops the bench on its own.

## Modbus register map

Matches the default OpenPLC v3 mapping (%QW0 = HR 0, %MW0 = HR 1024).

### Coils (FC01, read only from the HMI side)

| Coil | IEC    | Name           | Meaning                          |
|-----:|--------|----------------|----------------------------------|
| 0    | %QX0.0 | DRIVE_ENABLE   | Run signal to the inverter       |
| 1    | %QX0.1 | BRAKE_RELEASE  | Holding brake released           |
| 2    | %QX0.2 | RUN_LAMP       | 運転灯                            |
| 3    | %QX0.3 | FAULT_LAMP     | 異常灯                            |

### Status registers (PLC -> HMI)

| HR | IEC   | Name            | Unit / scale                                   |
|---:|-------|-----------------|------------------------------------------------|
| 0  | %QW0  | STATE           | 0 Idle, 1 Running, 2 Stopping, 3 Fault         |
| 1  | %QW1  | SPEED_ACT       | rpm                                            |
| 2  | %QW2  | TORQUE_ACT      | 0.1 N·m                                        |
| 3  | %QW3  | MOTOR_TEMP      | 0.1 °C                                         |
| 4  | %QW4  | ALARM_CODE      | first-out alarm, see below                     |
| 5  | %QW5  | HEARTBEAT       | +1 every scan, wraps 29999 -> 0                |
| 6  | %QW6  | SPEED_SP_ACTIVE | rpm, setpoint after clamping                   |
| 7  | %QW7  | LOAD_SP_ACTIVE  | %, setpoint after clamping                     |
| 8  | %QW8  | MAP_VERSION     | register map version, currently 1              |

The web HMI reads MAP_VERSION right after connecting, before it writes anything
(not even the watchdog). If it is not the version the HMI was built for, the HMI
stays disconnected and shows why, so it never writes commands into a different
PLC program. An older program without %QW8 reads as 0. Bump the version in
`bench_control.st` and `RegisterMap.CurrentMapVersion` together whenever an
address or its meaning changes.

### Command registers (HMI -> PLC)

| HR   | IEC  | Name         | Meaning                                              |
|-----:|------|--------------|------------------------------------------------------|
| 1024 | %MW0 | CMD          | 1 start, 2 stop, 3 reset. PLC writes 0 when taken.   |
| 1025 | %MW1 | SPEED_SP     | rpm, clamped to 0..3000                              |
| 1026 | %MW2 | LOAD_SP      | %, clamped to 0..100                                 |
| 1027 | %MW3 | SIM_ESTOP    | 1 = E-stop pressed (simulation only, see note)       |
| 1028 | %MW4 | HMI_WATCHDOG | HMI changes this value on every poll                 |
| 1029 | %MW5 | SIM_RUNAWAY  | 1 = drive ignores setpoint (fault injection)         |

Note on E-stop: on a real bench the E-stop is a hard-wired NC contact in the
safety circuit (and also read by the PLC as an input, e.g. %IX0.0). It is never
a Modbus register. SIM_ESTOP only exists so the alarm handling can be exercised
without hardware.

Status values live in `%QW` but are copied from internal variables at the end
of the scan. %QW is writable over Modbus in OpenPLC, so the program never uses
those registers as its own memory, otherwise a stray write from a client could
change the state machine.

## Command handshake

1. HMI writes the command code to HR 1024.
2. PLC reads it at the start of the next scan and writes 0 back.
3. HMI reads HR 1024 until it is 0 (gives up after 500 ms) to confirm the PLC took it.

One command register (instead of one coil per button) means a command can only
be executed once, and a start and a stop can never be active at the same time.

## State machine (PLC)

```
            start (no alarm)
   IDLE  --------------------->  RUNNING
    ^                              |
    | speed < 5 rpm                | stop
    |                              v
    +-------------------------  STOPPING

   any state --(alarm)--> FAULT --(reset, alarm cleared, speed < 5 rpm)--> IDLE
```

- DRIVE_ENABLE is TRUE only in RUNNING. In STOPPING and FAULT the motor coasts
  or ramps down.
- Reset is refused while the motor is still turning.

## Alarms

First-out: only the first alarm is latched in ALARM_CODE until reset, which is
what you want when one fault causes others.

| Code | Name                | 日本語        | Trips when                                | Can reset when          |
|-----:|---------------------|--------------|-------------------------------------------|-------------------------|
| 1    | E-stop              | 非常停止      | SIM_ESTOP = 1                             | E-stop released         |
| 2    | Overspeed           | 過速度        | speed > 3300 rpm                          | speed <= 3300 rpm       |
| 3    | Motor over-temp     | モーター過温度 | temp > 120.0 °C                           | temp < 110.0 °C         |
| 4    | HMI comm loss       | 通信異常      | watchdog unchanged 2 s while running      | watchdog changing again |

## Simulated bench (BenchSim FB)

Scan time is 20 ms. The model is simple on purpose, only good enough for the
HMI to have something realistic to show.

- Speed ramps to the setpoint at 600 rpm/s, ramps down at 900 rpm/s while
  enabled, coasts down at 400 rpm/s when the drive is disabled.
- Torque = load% × 50 N·m + 0.8 N·m friction + 0.0005 N·m/rpm × speed.
- Temperature is first order (τ = 30 s) towards
  25 °C + 0.04 × torque² + 0.005 × speed.

At 100 % load and 3000 rpm the steady-state temperature is about 149 °C, so the
over-temperature alarm trips after roughly 40 s. `profiles/overheat_demo.csv`
uses that to show the alarm path.

## Auto test sequence (HMI)

A profile is a CSV of steps: `speed_rpm, load_pct, dwell_s`.

For each step:

1. Write the setpoints.
2. **Settling** – wait until |speed − setpoint| ≤ 20 rpm continuously for 1 s.
   If that takes more than 20 s the run is aborted.
3. **Dwell** – record speed, torque and temperature for `dwell_s` seconds.
4. Store the step result (average speed, average torque, max temperature).

After the last step the bench is stopped and the step results are written to
a CSV.

Modes:

- **Manual** – operator sets speed/load and starts/stops the bench.
- **Auto** – the sequence above.
- **Hold** – the sequence is paused. The bench keeps running at the current
  setpoints and the settle/dwell timers stop counting. Resume continues where
  it left off.

A bench fault during a sequence aborts the sequence. The PLC has already
stopped the drive by then; the HMI just records why.

## Timing

| What                  | Period |
|-----------------------|--------|
| PLC scan              | 20 ms  |
| HMI poll              | 100 ms |
| PLC watchdog timeout  | 2 s    |
| Modbus request timeout| 1 s    |

On a request timeout the HMI drops the TCP connection rather than trying to
resync, because a late reply could otherwise be matched to the next request.
