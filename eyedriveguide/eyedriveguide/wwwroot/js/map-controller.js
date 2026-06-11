// map-controller.js — Leaflet map, position marker, route overlay

const MapController = (() => {
    let map = null;
    let posMarker = null;
    let routeLayer = null;
    let segmentLayers = [];

    function init() {
        if (map) return;
        map = L.map('map', { zoomControl: false, attributionControl: false })
               .setView([40.7128, -74.006], 13);

        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19
        }).addTo(map);

        L.control.zoom({ position: 'bottomright' }).addTo(map);

        posMarker = L.circleMarker([40.7128, -74.006], {
            radius: 10,
            fillColor: '#4a90d9',
            color: '#fff',
            weight: 3,
            fillOpacity: 1
        }).addTo(map);
    }

    function updatePosition(lat, lng) {
        if (!map) init();
        posMarker.setLatLng([lat, lng]);
        map.panTo([lat, lng], { animate: true, duration: 0.8 });
    }

    function drawRoute(routeData) {
        if (!map) return;
        clearRoute();

        if (!routeData || !routeData.segments) return;

        routeData.segments.forEach(seg => {
            const color = seg.isHighway ? '#4a90d9' : '#6c757d';
            const line = L.polyline([
                [seg.startCoord.lat, seg.startCoord.lng],
                [seg.endCoord.lat, seg.endCoord.lng]
            ], { color, weight: 4, opacity: 0.8 }).addTo(map);
            segmentLayers.push(line);
        });

        if (routeData.events) {
            routeData.events.forEach(ev => {
                const icon = ev.type === 'OnRamp' || ev.type === 'Merge' ? '🔀'
                           : ev.type === 'OffRamp' || ev.type === 'Exit' ? '🚪'
                           : '📍';
                const marker = L.marker([ev.coord.lat, ev.coord.lng], {
                    icon: L.divIcon({
                        className: '',
                        html: `<div style="font-size:1.3rem">${icon}</div>`,
                        iconAnchor: [12, 12]
                    })
                })
                .bindTooltip(ev.description || ev.type)
                .addTo(map);
                segmentLayers.push(marker);
            });
        }

        const coords = routeData.segments.flatMap(s => [
            [s.startCoord.lat, s.startCoord.lng],
            [s.endCoord.lat, s.endCoord.lng]
        ]);
        if (coords.length > 0) map.fitBounds(coords, { padding: [30, 30] });
    }

    function highlightSegment(index) {
        segmentLayers.forEach((l, i) => {
            if (l.setStyle) l.setStyle({ color: i === index ? '#f0a500' : '#4a90d9', weight: i === index ? 6 : 4 });
        });
    }

    function clearRoute() {
        segmentLayers.forEach(l => { if (l.remove) l.remove(); });
        segmentLayers = [];
    }

    return { init, updatePosition, drawRoute, highlightSegment, clearRoute };
})();
