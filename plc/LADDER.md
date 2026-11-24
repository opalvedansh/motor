# Ladder version (OpenPLC Editor)

`bench_control.st` is the reference program and runs as is. This page describes
the same logic split the way it is usually done on real machines:

- **Ladder (LD)** for the discrete logic: start/stop, alarm latching, reset,
  outputs.
- **Structured Text** function blocks for the analog side: clamping, the
  simulated bench and scaling the status values.

The register map does not change, so the HMI works with either version.

## Notation

```
--| |--   NO contact          --( )--   coil
--|/|--   NC contact          --(S)--   set (latch) coil
                              --(R)--   reset (unlatch) coil
[BLOCK]   function / function block, EN driven by the rung
```

## Project setup

1. New project in OpenPLC Editor.
2. Add a function block `BenchSim` (language ST), paste the FB from
   `bench_control.st`.
3. Add a function block `BenchIO` (language ST), code below.
4. Add the program `bench_main` (language **LD**).
5. In the resource, set the task interval to **20 ms** and attach `bench_main`.

### BenchIO (ST)

```
FUNCTION_BLOCK BenchIO
  VAR_INPUT
    drive_enable : BOOL;
    speed_sp_in  : INT;
    load_sp_in   : INT;
    runaway      : INT;
    state        : INT;
    alarm_code   : INT;
  END_VAR
  VAR_OUTPUT
    speed         : REAL;
    temp          : REAL;
    state_out     : INT;
    speed_out     : INT;
    torque_out    : INT;
    temp_out      : INT;
    alarm_out     : INT;
    heartbeat_out : INT;
    speed_sp_out  : INT;
    load_sp_out   : INT;
  END_VAR
  VAR
    sim      : BenchSim;
    hb       : INT;
    speed_sp : INT;
    load_sp  : INT;
  END_VAR

  speed_sp := LIMIT(0, speed_sp_in, 3000);
  load_sp  := LIMIT(0, load_sp_in, 100);

  sim(enable := drive_enable,
      speed_sp := INT_TO_REAL(speed_sp),
      load_pct := INT_TO_REAL(load_sp),
      runaway := runaway <> 0,
      dt := 0.02);

  speed := sim.speed;
  temp  := sim.temp;

  IF hb >= 29999 THEN
    hb := 0;
  ELSE
    hb := hb + 1;
  END_IF;

  state_out     := state;
  speed_out     := REAL_TO_INT(sim.speed);
  torque_out    := REAL_TO_INT(sim.torque * 10.0);
  temp_out      := REAL_TO_INT(sim.temp * 10.0);
  alarm_out     := alarm_code;
  heartbeat_out := hb;
  speed_sp_out  := speed_sp;
  load_sp_out   := load_sp;
END_FUNCTION_BLOCK
```

## Variables in bench_main

Located variables are the same as in `bench_control.st` (%QX0.0–0.3,
%QW0–7, %MW0–5). Internal ones:

| Name            | Type    | Use                                   |
|-----------------|---------|---------------------------------------|
| start_req       | BOOL    | CMD = 1 this scan                     |
| stop_req        | BOOL    | CMD = 2                               |
| reset_req       | BOOL    | CMD = 3                               |
| wd_last         | INT     | watchdog value from last scan         |
| wd_changed      | BOOL    |                                       |
| wd_timer        | TON     |                                       |
| wd_expired      | BOOL    | watchdog not changed for 2 s          |
| standstill      | BOOL    | speed < 5 rpm                         |
| estop           | BOOL    |                                       |
| overspeed       | BOOL    | speed > 3300 rpm                      |
| overtemp        | BOOL    | temp > 120 °C                         |
| hot             | BOOL    | temp >= 110 °C (reset hysteresis)     |
| al_is_overtemp  | BOOL    | ALARM_CODE = 3                        |
| commloss        | BOOL    |                                       |
| fault           | BOOL    | latched                               |
| reset_blocked   | BOOL    |                                       |
| running         | BOOL    | seal-in                               |
| stopping        | BOOL    |                                       |
| state           | INT     | 0..3, goes to BenchIO                 |
| alarm_code      | INT     | first-out code                        |
| io              | BenchIO |                                       |

## Rungs

Rung order matters: the PLC scans top to bottom, and the alarm rungs use that
to get first-out behaviour.

### 1. Decode the command

```
        +-----+
 -------| EQ  |-----------------------------------------( start_req )
   cmd -|IN1  |
     1 -|IN2  |
        +-----+
```

Same for `stop_req` (2) and `reset_req` (3).

### 2. Clear the command

```
        +------+
 -------| MOVE |
     0 -|IN OUT|- cmd
        +------+
```

The HMI waits for CMD to go back to 0 to know the command was taken.

### 3. Watchdog

```
        +-----+
 -------| NE  |-----------------------------------------( wd_changed )
 hmi_watchdog -|IN1|
   wd_last    -|IN2|
        +-----+

        +------+
 -------| MOVE |
 hmi_watchdog -|IN OUT|- wd_last
        +------+

                     +-----------+
 ----|/ wd_changed|--|IN  TON   Q|-------------------------( wd_expired )
              T#2s --|PT         |
                     +-----------+
```

### 4. Comparisons

One rung each, comparison block driving a coil:

| Block                       | Coil            |
|-----------------------------|-----------------|
| LT(io.speed, 5.0)           | standstill      |
| GT(io.speed, 3300.0)        | overspeed       |
| GT(io.temp, 120.0)          | overtemp        |
| GE(io.temp, 110.0)          | hot             |
| NE(sim_estop, 0)            | estop           |
| EQ(alarm_code, 3)           | al_is_overtemp  |

### 5. Comm loss (only while the motor is driven)

```
 ----| wd_expired |--+--| running  |--+-------------------( commloss )
                     |               |
                     +--| stopping |--+
```

### 6–9. Alarm latch, first-out

```
 ----| estop |-----|/ fault |--+--[MOVE 1 -> alarm_code]
                               |
                               +--------------------------(S fault )
```

Then the same rung for `overspeed` (2), `overtemp` (3) and `commloss` (4), in
that order. Once a rung has set `fault`, the `|/ fault|` contact in the rungs
below is open for the rest of the scan, so only the first alarm writes its
code.

### 10. What still blocks a reset

```
 ---+--| estop          |-----------------+--------------( reset_blocked )
    |                                     |
    +--| overspeed      |-----------------+
    |                                     |
    +--| overtemp       |-----------------+
    |                                     |
    +--| al_is_overtemp |----| hot |------+
```

### 11. Reset

```
 ----| reset_req |--| fault |--| standstill |--|/ reset_blocked |--+--(R fault )
                                                                   |
                                                                   +--[MOVE 0 -> alarm_code]
```

### 12. Running (seal-in / 自己保持)

```
 ---+--| start_req |--+--|/ fault |--|/ stop_req |-------------( running )
    |                 |
    +--| running   |--+
```

### 13. Stopping

```
 ----|/ running |--|/ fault |--|/ standstill |-----------------( stopping )
```

### 14. Outputs

```
 ----| running |--|/ fault |-----------------------------------( drive_enable )

 ---+--| drive_enable |--+-------------------------------------( brake_release )
    |                    |
    +--|/ standstill  |--+

 ----| running |-----------------------------------------------( run_lamp )

 ----| fault |-------------------------------------------------( fault_lamp )
```

### 15. State code for the HMI

```
 ----|/ running |--|/ stopping |--|/ fault |--[MOVE 0 -> state]
 ----| running  |--|/ fault |--------------[MOVE 1 -> state]
 ----| stopping |-------------------------[MOVE 2 -> state]
 ----| fault    |-------------------------[MOVE 3 -> state]
```

### 16. Bench I/O block

```
          +--------------------------+
 ---------|EN        BenchIO         |
 drive_enable -|drive_enable  state_out|- state_out      (%QW0)
 speed_sp_in  -|speed_sp_in   speed_out|- speed_out      (%QW1)
 load_sp_in   -|load_sp_in   torque_out|- torque_out     (%QW2)
 sim_runaway  -|runaway        temp_out|- temp_out       (%QW3)
 state        -|state         alarm_out|- alarm_out      (%QW4)
 alarm_code   -|alarm_code heartbeat_out|- heartbeat_out (%QW5)
               |           speed_sp_out|- speed_sp_out   (%QW6)
               |            load_sp_out|- load_sp_out    (%QW7)
               +--------------------------+
```

`io.speed` and `io.temp` are read by rung 4 in the next scan, same as the ST
version.

### 17. Register map version

```
 ---------------------------------------[MOVE 1 -> map_version_out]   (%QW8)
```

Declare `map_version_out AT %QW8 : INT`. The web HMI refuses to connect to a
program that does not report the version it was built for (docs/design.md).

## Checking it

Compile, upload the generated `.st` to the runtime, connect the HMI and go
through this list:

- [ ] Start / stop, speed follows the setpoint
- [ ] Reset is refused while the motor is still turning
- [ ] Sim E-stop while running → alarm 1, drive off, reset only after release
- [ ] `overheat_demo.csv` → alarm 3 after ~40 s at full load, reset only below 110 °C
- [ ] Close the HMI while running → alarm 4 after 2 s
- [ ] Sim runaway → alarm 2
- [ ] E-stop and runaway together → only the first one is in ALARM_CODE
- [ ] Web HMI connects (a missing rung 17 shows "register map version 0")
