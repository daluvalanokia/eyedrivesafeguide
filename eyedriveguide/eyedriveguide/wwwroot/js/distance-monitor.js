// ============================================================
// distance-monitor.js
// Front-end following distance sensor module.
//
// Two sensor sources supported (first available wins):
//   1. TIME-TO-COLLISION via front camera optical flow (built-in)
//      Uses the front-facing camera to detect relative motion
//      of the leading vehicle and estimate distance via apparent
//      size change rate (TTC proxy, not true metres).
//
//   2. HARDWARE SENSOR via WebSerial (USB/BT ultrasonic or LIDAR)
//      If a serial device is connected, reads distance in cm,
//      converts to metres. Format: "D:<value_cm>\n" per line.
//
// Output: calls onDistance(metres) on every measurement tick.
// If no sensor available, calls onDistance(null).
// ============================================================

const DistanceMonitor = (() => {

  // ── Configuration ──────────────────────────────────────────
  const TTC_FRAME_INTERVAL_MS = 500;  // Optical flow sample rate
  const SERIAL_BAUD_RATE      = 9600;
  const SERIAL_MAX_RANGE_M    = 5.0;  // Ignore ultrasonic readings > 5 m (likely noise on cheap sensors; increase for LIDAR)
  const TTC_SCALE_FACTOR      = 12.0; // Tuning constant: px/frame → metres (calibrate per camera)
  const MIN_MOTION_PX         = 1.5;  // Ignore sub-pixel apparent motion noise

  // ── State ─────────────────────────────────────────────────
  let _onDistance = null;
  let _source = null;           // 'camera' | 'serial' | null
  let _serialReader = null;
  let _serialPort = null;
  let _cameraStream = null;
  let _cameraInterval = null;
  let _canvas = null;
  let _ctx = null;
  let _video = null;
  let _prevFrame = null;
  let _runningEstimateM = null; // Smoothed estimate (EMA)
  const EMA_ALPHA = 0.4;        // Exponential moving average weight

  // ── Public: start ─────────────────────────────────────────
  async function start(onDistance) {
    _onDistance = onDistance;

    // Try WebSerial first (more accurate if hardware available)
    if ('serial' in navigator) {
      const serialOk = await _trySerial();
      if (serialOk) { _source = 'serial'; return; }
    }

    // Fall back to front camera TTC
    const cameraOk = await _tryCamera();
    if (cameraOk) { _source = 'camera'; return; }

    // No sensor available
    console.warn('[DistanceMonitor] No distance sensor available');
    _onDistance(null);
  }

  // ── Public: stop ──────────────────────────────────────────
  function stop() {
    _stopCamera();
    _stopSerial();
    _source = null;
    _runningEstimateM = null;
  }

  // ── Public: source info ───────────────────────────────────
  function getSource() { return _source; }

  // ═══════════════════════════════════════════════════════════
  // Camera TTC (time-to-collision proxy)
  // Measures apparent motion of the central region between frames.
  // A vehicle filling more of the frame = getting closer.
  // ═══════════════════════════════════════════════════════════

  async function _tryCamera() {
    try {
      _cameraStream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: 'user', width: 160, height: 120 }
        // Use 'environment' for rear camera if sensor on back;
        // 'user' for forward-facing dashboard mount
      });

      _video = document.createElement('video');
      _video.srcObject = _cameraStream;
      _video.setAttribute('playsinline', true);
      _video.muted = true;
      await _video.play();

      _canvas = document.createElement('canvas');
      _canvas.width = 160;
      _canvas.height = 120;
      _ctx = _canvas.getContext('2d', { willReadFrequently: true });

      _cameraInterval = setInterval(_cameraTick, TTC_FRAME_INTERVAL_MS);
      console.log('[DistanceMonitor] Front camera TTC active');
      return true;
    } catch (e) {
      console.warn('[DistanceMonitor] Camera unavailable:', e.message);
      return false;
    }
  }

  function _cameraTick() {
    if (!_ctx || !_video) return;
    _ctx.drawImage(_video, 0, 0, 160, 120);
    const frame = _ctx.getImageData(0, 0, 160, 120);

    if (_prevFrame) {
      // Focus on the central 60×60 region (where lead vehicle appears)
      const motionPx = _computeCentralMotion(_prevFrame, frame, 50, 30, 60, 60);

      if (motionPx >= MIN_MOTION_PX) {
        // TTC proxy: apparent size change → distance estimate
        // Higher motion per frame = vehicle is closer / approaching faster
        // This is a heuristic; real accuracy requires calibration
        const rawEstimateM = TTC_SCALE_FACTOR / motionPx;
        const clampedM = Math.min(rawEstimateM, 120); // Cap at 120 m (sensor range)

        // Smooth with exponential moving average
        _runningEstimateM = _runningEstimateM === null
          ? clampedM
          : EMA_ALPHA * clampedM + (1 - EMA_ALPHA) * _runningEstimateM;

        _onDistance(Math.round(_runningEstimateM * 10) / 10);
      } else {
        // Very low motion = vehicle far away or not in frame
        // Decay estimate upward (vehicle getting further away)
        if (_runningEstimateM !== null) {
          _runningEstimateM = Math.min(_runningEstimateM * 1.1, 120);
          _onDistance(Math.round(_runningEstimateM * 10) / 10);
        } else {
          _onDistance(null); // No data
        }
      }
    }

    _prevFrame = frame;
  }

  function _computeCentralMotion(prev, curr, cx, cy, w, h) {
    let diff = 0;
    let count = 0;
    const imgW = 160;
    for (let y = cy; y < cy + h; y++) {
      for (let x = cx; x < cx + w; x++) {
        const i = (y * imgW + x) * 4;
        diff += Math.abs(prev.data[i] - curr.data[i]);     // R
        diff += Math.abs(prev.data[i+1] - curr.data[i+1]); // G
        diff += Math.abs(prev.data[i+2] - curr.data[i+2]); // B
        count++;
      }
    }
    return count > 0 ? (diff / count / 3 / 255 * 100) : 0;
  }

  function _stopCamera() {
    if (_cameraInterval) { clearInterval(_cameraInterval); _cameraInterval = null; }
    if (_cameraStream)   { _cameraStream.getTracks().forEach(t => t.stop()); _cameraStream = null; }
    _video = null; _canvas = null; _ctx = null; _prevFrame = null;
  }

  // ═══════════════════════════════════════════════════════════
  // WebSerial hardware sensor (ultrasonic / LIDAR)
  // Device must send: "D:<distance_cm>\n" lines
  // ═══════════════════════════════════════════════════════════

  async function _trySerial() {
    try {
      // Note: requestPort() requires a user gesture — call this from a button handler
      _serialPort = await navigator.serial.requestPort();
      await _serialPort.open({ baudRate: SERIAL_BAUD_RATE });

      _serialReader = _serialPort.readable.getReader();
      _readSerial(); // Start async read loop
      console.log('[DistanceMonitor] WebSerial sensor active');
      return true;
    } catch (e) {
      console.warn('[DistanceMonitor] Serial unavailable:', e.message);
      return false;
    }
  }

  async function _readSerial() {
    const decoder = new TextDecoder();
    let buffer = '';

    try {
      while (true) {
        const { value, done } = await _serialReader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() ?? '';

        for (const line of lines) {
          const match = line.trim().match(/^D:(\d+(?:\.\d+)?)$/);
          if (match) {
            const cm = parseFloat(match[1]);
            const metres = cm / 100;

            // Ignore out-of-range readings (sensor noise)
            if (metres > 0 && metres <= SERIAL_MAX_RANGE_M) {
              _runningEstimateM = _runningEstimateM === null
                ? metres
                : EMA_ALPHA * metres + (1 - EMA_ALPHA) * _runningEstimateM;
              _onDistance(Math.round(_runningEstimateM * 10) / 10);
            }
          }
        }
      }
    } catch (e) {
      console.warn('[DistanceMonitor] Serial read error:', e.message);
      _onDistance(null);
    }
  }

  async function _stopSerial() {
    try {
      if (_serialReader) { await _serialReader.cancel(); _serialReader = null; }
      if (_serialPort)   { await _serialPort.close();   _serialPort = null; }
    } catch {}
  }

  // ── Public API ─────────────────────────────────────────────
  return { start, stop, getSource };

})();
