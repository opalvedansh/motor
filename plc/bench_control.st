(*
  Motor test bench - control program for OpenPLC Runtime v3

  Register map and behaviour: docs/design.md
  The task interval must be 20 ms (CYCLE_S in bench_main).

  BenchSim stands in for the inverter, the dynamometer and the sensors so the
  program can run on a PC without hardware. On a real bench it is replaced by
  physical I/O (%IW for speed/torque/temperature, %QW for the drive setpoint).
*)

FUNCTION_BLOCK BenchSim
  VAR_INPUT
    enable   : BOOL;
    speed_sp : REAL;   (* rpm *)
    load_pct : REAL;   (* 0..100, torque command to the dyno *)
    runaway  : BOOL;   (* fault injection: drive ignores the setpoint *)
    dt       : REAL;   (* s *)
  END_VAR
  VAR_OUTPUT
    speed  : REAL;            (* rpm *)
    torque : REAL;            (* N.m *)
    temp   : REAL := 25.0;    (* deg C, motor winding *)
  END_VAR
  VAR
    target : REAL;
    rise   : REAL;
  END_VAR
  VAR CONSTANT
    ACCEL        : REAL := 600.0;    (* rpm/s *)
    DECEL        : REAL := 900.0;    (* rpm/s, drive enabled *)
    COAST        : REAL := 400.0;    (* rpm/s, drive disabled *)
    RUNAWAY_RPM  : REAL := 3600.0;
    RATED_TORQUE : REAL := 50.0;     (* N.m at 100 % load *)
    FRICTION     : REAL := 0.8;      (* N.m *)
    VISCOUS      : REAL := 0.0005;   (* N.m per rpm *)
    AMBIENT      : REAL := 25.0;     (* deg C *)
    K_COPPER     : REAL := 0.04;     (* deg C per (N.m)^2 *)
    K_IRON       : REAL := 0.005;    (* deg C per rpm *)
    TAU          : REAL := 30.0;     (* s, thermal time constant *)
  END_VAR

  IF enable THEN
    IF runaway THEN
      target := RUNAWAY_RPM;
    ELSE
      target := speed_sp;
    END_IF;
  ELSE
    target := 0.0;
  END_IF;

  IF speed < target THEN
    speed := MIN(speed + ACCEL * dt, target);
  ELSIF enable THEN
    speed := MAX(speed - DECEL * dt, target);
  ELSE
    speed := MAX(speed - COAST * dt, 0.0);
  END_IF;

  IF speed > 1.0 THEN
    torque := load_pct * 0.01 * RATED_TORQUE + FRICTION + VISCOUS * speed;
  ELSE
    torque := 0.0;
  END_IF;

  (* first-order thermal model *)
  rise := K_COPPER * torque * torque + K_IRON * speed;
  temp := temp + (AMBIENT + rise - temp) * dt / TAU;
END_FUNCTION_BLOCK


PROGRAM bench_main
  VAR
    (* field outputs *)
    drive_enable  AT %QX0.0 : BOOL;
    brake_release AT %QX0.1 : BOOL;
    run_lamp      AT %QX0.2 : BOOL;
    fault_lamp    AT %QX0.3 : BOOL;

    (* status to HMI, HR 0..7 *)
    state_out     AT %QW0 : INT;
    speed_out     AT %QW1 : INT;
    torque_out    AT %QW2 : INT;
    temp_out      AT %QW3 : INT;
    alarm_out     AT %QW4 : INT;
    heartbeat_out AT %QW5 : INT;
    speed_sp_out  AT %QW6 : INT;
    load_sp_out   AT %QW7 : INT;
    map_version_out AT %QW8 : INT;

    (* commands from HMI, HR 1024..1029 *)
    cmd           AT %MW0 : INT;
    speed_sp_in   AT %MW1 : INT;
    load_sp_in    AT %MW2 : INT;
    sim_estop     AT %MW3 : INT;
    hmi_watchdog  AT %MW4 : INT;
    sim_runaway   AT %MW5 : INT;
  END_VAR
  VAR
    state      : INT := 0;   (* 0 idle, 1 running, 2 stopping, 3 fault *)
    alarm_code : INT := 0;   (* first-out *)
    heartbeat  : INT := 0;

    start_req : BOOL;
    stop_req  : BOOL;
    reset_req : BOOL;
    speed_sp  : INT;
    load_sp   : INT;

    estop_active     : BOOL;
    overspeed_active : BOOL;
    overtemp_active  : BOOL;
    commloss_active  : BOOL;
    reset_blocked    : BOOL;
    standstill       : BOOL;

    wd_last    : INT;
    wd_changed : BOOL;
    wd_timer   : TON;

    sim : BenchSim;
  END_VAR
  VAR CONSTANT
    CYCLE_S        : REAL := 0.02;   (* must match the task interval *)
    MAX_SPEED      : INT := 3000;    (* rpm *)
    OVERSPEED_RPM  : REAL := 3300.0;
    OVERTEMP_TRIP  : REAL := 120.0;
    OVERTEMP_RESET : REAL := 110.0;
    STANDSTILL_RPM : REAL := 5.0;
    MAP_VERSION    : INT := 1;       (* register map version, see docs/design.md *)
  END_VAR

  (* 1. commands: take once, then clear so the HMI sees it was accepted *)
  start_req := cmd = 1;
  stop_req  := cmd = 2;
  reset_req := cmd = 3;
  cmd := 0;

  speed_sp := LIMIT(0, speed_sp_in, MAX_SPEED);
  load_sp  := LIMIT(0, load_sp_in, 100);

  (* 2. HMI watchdog: the HMI changes hmi_watchdog on every poll *)
  wd_changed := hmi_watchdog <> wd_last;
  wd_last := hmi_watchdog;
  wd_timer(IN := NOT wd_changed, PT := T#2s);

  (* 3. alarms, latched first-out *)
  standstill       := sim.speed < STANDSTILL_RPM;
  estop_active     := sim_estop <> 0;
  overspeed_active := sim.speed > OVERSPEED_RPM;
  overtemp_active  := sim.temp > OVERTEMP_TRIP;
  commloss_active  := wd_timer.Q AND (state = 1 OR state = 2);

  IF alarm_code = 0 THEN
    IF estop_active THEN
      alarm_code := 1;
    ELSIF overspeed_active THEN
      alarm_code := 2;
    ELSIF overtemp_active THEN
      alarm_code := 3;
    ELSIF commloss_active THEN
      alarm_code := 4;
    END_IF;
  END_IF;

  (* over-temp needs to cool down to 110 C before it can be reset *)
  reset_blocked := estop_active OR overspeed_active OR overtemp_active
                   OR (alarm_code = 3 AND sim.temp >= OVERTEMP_RESET);

  (* 4. state machine *)
  IF alarm_code <> 0 THEN
    state := 3;
  END_IF;

  CASE state OF
    0: (* IDLE *)
      IF start_req THEN
        state := 1;
      END_IF;

    1: (* RUNNING *)
      IF stop_req THEN
        state := 2;
      END_IF;

    2: (* STOPPING *)
      IF start_req THEN
        state := 1;
      ELSIF standstill THEN
        state := 0;
      END_IF;

    3: (* FAULT *)
      IF reset_req AND NOT reset_blocked AND standstill THEN
        alarm_code := 0;
        state := 0;
      END_IF;
  ELSE
    state := 0;
  END_CASE;

  (* 5. outputs *)
  drive_enable  := state = 1;
  brake_release := drive_enable OR NOT standstill;
  run_lamp      := state = 1;
  fault_lamp    := state = 3;

  (* 6. simulated drive + dyno + sensors *)
  sim(enable := drive_enable,
      speed_sp := INT_TO_REAL(speed_sp),
      load_pct := INT_TO_REAL(load_sp),
      runaway := sim_runaway <> 0,
      dt := CYCLE_S);

  (* 7. status to HMI. Copied from internal variables because %QW is
        writable over Modbus and must not be used as program memory. *)
  IF heartbeat >= 29999 THEN
    heartbeat := 0;
  ELSE
    heartbeat := heartbeat + 1;
  END_IF;

  state_out     := state;
  speed_out     := REAL_TO_INT(sim.speed);
  torque_out    := REAL_TO_INT(sim.torque * 10.0);
  temp_out      := REAL_TO_INT(sim.temp * 10.0);
  alarm_out     := alarm_code;
  heartbeat_out := heartbeat;
  speed_sp_out  := speed_sp;
  load_sp_out   := load_sp;
  map_version_out := MAP_VERSION;
END_PROGRAM


CONFIGURATION Config0
  RESOURCE Res0 ON PLC
    TASK task0(INTERVAL := T#20ms, PRIORITY := 0);
    PROGRAM instance0 WITH task0 : bench_main;
  END_RESOURCE
END_CONFIGURATION
