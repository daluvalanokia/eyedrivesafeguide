// alert-system.js — renders alert banners and plays voice/audio cues

const AlertSystem = (() => {
    const container = () => document.getElementById('alertContainer');
    const blindSpot = () => document.getElementById('blindSpotBanner');
    let blindSpotTimer = null;
    let speechAvailable = 'speechSynthesis' in window;

    const iconMap = {
        'merge':       '🔀',
        'exit':        '🚪',
        'lane':        '🛣️',
        'speed-red':   '🔴',
        'speed-yellow':'🟡',
        'distraction': '🔊',
        'backing':     '⚠️',
        'info':        'ℹ️'
    };

    function show(alert) {
        const { type, message, severity, blindSpotHold } = alert;

        const el = document.createElement('div');
        el.className = `edg-alert-banner edg-alert-${severity || 'info'}`;
        el.innerHTML = `<span>${iconMap[type] || '🔔'}</span><span>${message}</span>`;
        el.dataset.type = type;

        const c = container();
        if (!c) return;

        const existing = c.querySelector(`[data-type="${type}"]`);
        if (existing) existing.remove();

        c.prepend(el);

        const ttl = severity === 'danger' ? 8000 : 5000;
        setTimeout(() => el.remove(), ttl);

        speak(message);

        if (blindSpotHold) showBlindSpot();
    }

    function showBlindSpot() {
        const el = blindSpot();
        if (!el) return;
        el.style.display = 'block';
        if (blindSpotTimer) clearTimeout(blindSpotTimer);
        blindSpotTimer = setTimeout(() => { el.style.display = 'none'; }, 3000);
    }

    function speak(msg) {
        if (!speechAvailable) return;
        window.speechSynthesis.cancel();
        const u = new SpeechSynthesisUtterance(msg);
        u.rate = 1.05;
        u.pitch = 1.0;
        u.volume = 1.0;
        window.speechSynthesis.speak(u);
    }

    function clear() {
        const c = container();
        if (c) c.innerHTML = '';
    }

    return { show, speak, clear, showBlindSpot };
})();
