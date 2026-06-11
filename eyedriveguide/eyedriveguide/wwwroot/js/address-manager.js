// address-manager.js — CRUD for the Configuration > Addresses tab

let addrModal = null;

document.addEventListener('DOMContentLoaded', () => {
    addrModal = new bootstrap.Modal(document.getElementById('addressModal'));
});

function openAddressModal(data) {
    document.getElementById('addrId').value = data?.id || 0;
    document.getElementById('addrLabel').value = data?.label || '';
    document.getElementById('addrType').value = data?.type ?? 2;
    document.getElementById('addrStreet').value = data?.streetAddress || '';
    document.getElementById('addrCity').value = data?.city || '';
    document.getElementById('addrState').value = data?.state || '';
    document.getElementById('addrZip').value = data?.zipCode || '';
    document.getElementById('addrCountry').value = data?.country || 'US';
    document.getElementById('addrLat').value = data?.latitude ?? '';
    document.getElementById('addrLng').value = data?.longitude ?? '';
    document.getElementById('addrError').style.display = 'none';
    document.getElementById('addressModalTitle').textContent = data ? 'Edit Address' : 'Add Address';
    addrModal.show();
}

function useCurrentLocationForAddress() {
    if (!navigator.geolocation) return;
    navigator.geolocation.getCurrentPosition(
        pos => {
            document.getElementById('addrLat').value = pos.coords.latitude.toFixed(6);
            document.getElementById('addrLng').value = pos.coords.longitude.toFixed(6);
        },
        () => alert('Could not get location. Please enter coordinates manually.')
    );
}

async function editAddress(id) {
    const res = await fetch(`/api/addresses/${id}`);
    if (!res.ok) return;
    const data = await res.json();
    openAddressModal(data);
}

async function deleteAddress(id) {
    if (!confirm('Delete this address?')) return;
    const res = await fetch(`/api/addresses/${id}`, { method: 'DELETE' });
    if (res.ok) {
        const el = document.querySelector(`[data-id="${id}"]`);
        if (el) el.remove();
        checkEmpty();
    }
}

async function saveAddress() {
    const id = parseInt(document.getElementById('addrId').value);
    const latVal = parseFloat(document.getElementById('addrLat').value);
    const lngVal = parseFloat(document.getElementById('addrLng').value);
    const body = {
        id,
        label: document.getElementById('addrLabel').value.trim(),
        type: parseInt(document.getElementById('addrType').value),
        streetAddress: document.getElementById('addrStreet').value.trim(),
        city: document.getElementById('addrCity').value.trim(),
        state: document.getElementById('addrState').value.trim(),
        zipCode: document.getElementById('addrZip').value.trim(),
        country: document.getElementById('addrCountry').value.trim(),
        latitude: isNaN(latVal) ? null : latVal,
        longitude: isNaN(lngVal) ? null : lngVal
    };

    if (!body.label || !body.streetAddress) {
        document.getElementById('addrError').textContent = 'Label and street address are required.';
        document.getElementById('addrError').style.display = '';
        return;
    }

    const url = id ? `/api/addresses/${id}` : '/api/addresses';
    const method = id ? 'PUT' : 'POST';
    const res = await fetch(url, {
        method, headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body)
    });

    if (!res.ok) {
        document.getElementById('addrError').textContent = 'Save failed. Please try again.';
        document.getElementById('addrError').style.display = '';
        return;
    }

    const saved = await res.json();
    addrModal.hide();
    refreshAddressList();
}

async function refreshAddressList() {
    const res = await fetch('/api/addresses');
    const list = await res.json();
    const container = document.getElementById('addressList');
    const empty = document.getElementById('noAddresses');

    const typeLabels = ['Home', 'Work', 'Frequent'];
    const typeBadge = ['home', 'work', 'frequent'];

    container.innerHTML = list.map(a => `
        <div class="list-group-item edg-addr-item d-flex justify-content-between align-items-start" data-id="${a.id}">
            <div>
                <span class="badge edg-badge-${typeBadge[a.type] || 'frequent'} me-2">${typeLabels[a.type] || 'Frequent'}</span>
                <strong>${escHtml(a.label)}</strong>
                <div class="text-muted small">${escHtml(a.fullAddress || a.streetAddress)}</div>
            </div>
            <div class="d-flex gap-2 ms-2">
                <button class="btn btn-sm btn-outline-secondary" onclick="editAddress(${a.id})">✏️</button>
                <button class="btn btn-sm btn-outline-danger" onclick="deleteAddress(${a.id})">🗑️</button>
            </div>
        </div>
    `).join('');

    empty.style.display = list.length === 0 ? '' : 'none';
}

function checkEmpty() {
    const container = document.getElementById('addressList');
    const empty = document.getElementById('noAddresses');
    if (empty) empty.style.display = container.children.length === 0 ? '' : 'none';
}

function escHtml(s) {
    return (s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}
