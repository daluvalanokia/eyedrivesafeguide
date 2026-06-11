// sensor-monitor.js — GPS, microphone dB, accelerometer, backing detection (optical flow)

const SensorMonitor = (() => {
    let watchId = null;
    let audioCtx = null;
    let analyser = null;
    let micStream = null;
    let accelMag = null;
    let backingCanvas = null;
    let backingCtx = null;
    let backingVideo = null;
    let backingStream = null;
    let prevFrame = null;
    let backingInterval = null;

    const settings = window.EDG_SETTINGS || {};

    // ── GPS ──────────────────────────────────────────────────────────────────
    function startGps(onPosition, onError) {
        if (!navigator.geolocation) { onError('Geolocation not supported'); return; }
        watchId = navigator.geolocation.watchPosition(
            pos => {
                const { latitude: lat, longitude: lng, speed } = pos.coords;
                const kmh = speed != null ? speed * 3.6 : 0;
                onPosition(lat, lng, kmh);
            },
            err => onError(err.message),
            { enableHighAccuracy: true, maximumAge: 1000, timeout: 10000 }
        );
    }

    function stopGps() {
        if (watchId != null) { navigator.geolocation.clearWatch(watchId); watchId = null; }
    }

    // ── Microphone dB ────────────────────────────────────────────────────────
    async function startMicrophone(onDb) {
        try {
            micStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
            audioCtx = new (window.AudioContext || window.webkitAudioContext)();
            analyser = audioCtx.createAnalyser();
            analyser.fftSize = 2048;
            audioCtx.createMediaStreamSource(micStream).connect(analyser);

            const buf = new Uint8Array(analyser.frequencyBinCount);
            function tick() {
                if (!analyser) return;
                analyser.getByteFrequencyData(buf);
                const sum = buf.reduce((a, b) => a + b, 0);
                const avg = sum / buf.length;
                const db = 20 * Math.log10(avg / 255 + 1e-9) + 90;
                onDb(Math.max(0, db));
                requestAnimationFrame(tick);
            }
            tick();
            return true;
        } catch {
            return false;
        }
    }

    function stopMicrophone() {
        if (micStream) { micStream.getTracks().forEach(t => t.stop()); micStream = null; }
        if (audioCtx) { audioCtx.close(); audioCtx = null; analyser = null; }
    }

    // ── Accelerometer ────────────────────────────────────────────────────────
    function startAccelerometer(onAccel) {
        function handler(e) {
            const x = e.accelerationIncludingGravity?.x || 0;
            const y = e.accelerationIncludingGravity?.y || 0;
            const z = e.accelerationIncludingGravity?.z || 0;
            accelMag = Math.sqrt(x * x + y * y + z * z);
            onAccel(accelMag);
        }
        window.addEventListener('devicemotion', handler);
        return () => window.removeEventListener('devicemotion', handler);
    }

    // ── Backing (rear camera optical flow) ────────────────────────────────────
    async function startBacking(onBacking) {
        try {
            backingStream = await navigator.mediaDevices.getUserMedia({
                video: { facingMode: { exact: 'environment' }, width: 160, height: 120 }
            });
            backingVideo = document.createElement('video');
            backingVideo.srcObject = backingStream;
            backingVideo.setAttribute('playsinline', true);
            backingVideo.play();

            backingCanvas = document.createElement('canvas');
            backingCanvas.width = 160; backingCanvas.height = 120;
            backingCtx = backingCanvas.getContext('2d');
            prevFrame = null;

            backingInterval = setInterval(() => {
                if (!backingCtx) return;
                backingCtx.drawImage(backingVideo, 0, 0, 160, 120);
                const cur = backingCtx.getImageData(0, 0, 160, 120);
                if (prevFrame) {
                    const motion = computeFrameDelta(prevFrame, cur);
                    if (motion > 18) onBacking(motion);
                }
                prevFrame = cur;
            }, 500);
            return true;
        } catch {
            return false;
        }
    }

    function computeFrameDelta(a, b) {
        let diff = 0;
        for (let i = 0; i < a.data.length; i += 16) {
            diff += Math.abs(a.data[i] - b.data[i]);
        }
        return diff / (a.data.length / 16) / 255 * 100;
    }

    function stopBacking() {
        if (backingInterval) { clearInterval(backingInterval); backingInterval = null; }
        if (backingStream) { backingStream.getTracks().forEach(t => t.stop()); backingStream = null; }
        backingCtx = null; backingCanvas = null; backingVideo = null; prevFrame = null;
    }

    async function requestPermissions() {
        const results = { gps: false, mic: false, camera: false };
        try { await new Promise((res, rej) => navigator.geolocation.getCurrentPosition(res, rej)); results.gps = true; } catch {}
        try { const s = await navigator.mediaDevices.getUserMedia({ audio: true }); s.getTracks().forEach(t => t.stop()); results.mic = true; } catch {}
        try { const s = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } }); s.getTracks().forEach(t => t.stop()); results.camera = true; } catch {}
        return results;
    }

    return { startGps, stopGps, startMicrophone, stopMicrophone, startAccelerometer, startBacking, stopBacking, requestPermissions };
})();
