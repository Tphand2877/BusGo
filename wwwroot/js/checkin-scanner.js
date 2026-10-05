/**
 * BusGo CheckIn Camera & Barcode Scanner
 * Uses html5-qrcode (Html5Qrcode class) with Web Audio API feedback.
 * Supports camera mirroring (flip horizontal) and full bilingual (vi-VN / en-US).
 * Note: getUserMedia requires HTTPS or localhost per Web/W3C standards.
 */
(() => {
    'use strict';

    // Bilingual dictionary & helper
    const isEn = (document.documentElement.lang || '').toLowerCase().startsWith('en');
    const t = (vi, en) => isEn ? en : vi;

    let html5QrCode = null;
    let currentCameraId = null;
    let isScanning = false;
    let isPausedForFeedback = false;
    let lastScannedCode = '';
    let lastScannedTime = 0;
    let isMuted = localStorage.getItem('busgo_checkin_muted') === 'true';
    let isMirrored = localStorage.getItem('busgo_checkin_mirrored') === 'true';
    let dismissTimer = null;

    // Web Audio API feedback
    const AudioContext = window.AudioContext || window.webkitAudioContext;
    let audioCtx = null;

    function playTone(freq, type, duration, delay = 0) {
        if (isMuted) return;
        try {
            if (!audioCtx) audioCtx = new AudioContext();
            if (audioCtx.state === 'suspended') audioCtx.resume();
            const osc = audioCtx.createOscillator();
            const gain = audioCtx.createGain();
            osc.type = type;
            osc.frequency.setValueAtTime(freq, audioCtx.currentTime + delay);
            gain.gain.setValueAtTime(0.18, audioCtx.currentTime + delay);
            gain.gain.exponentialRampToValueAtTime(0.001, audioCtx.currentTime + delay + duration);
            osc.connect(gain);
            gain.connect(audioCtx.destination);
            osc.start(audioCtx.currentTime + delay);
            osc.stop(audioCtx.currentTime + delay + duration);
        } catch (e) {}
    }

    function playSuccessSound() {
        playTone(587.33, 'sine', 0.12, 0);     // D5
        playTone(880.00, 'sine', 0.24, 0.11);  // A5
    }

    function playWarningSound() {
        playTone(440.00, 'triangle', 0.16, 0);    // A4
        playTone(349.23, 'triangle', 0.22, 0.12); // F4
    }

    function playErrorSound() {
        playTone(220.00, 'sawtooth', 0.14, 0);    // A3
        playTone(174.61, 'sawtooth', 0.26, 0.12); // F3
    }

    function applyMirrorState(mirrored) {
        isMirrored = mirrored;
        localStorage.setItem('busgo_checkin_mirrored', String(mirrored));
        const reader = document.querySelector('#qr-reader');
        if (reader) {
            reader.classList.toggle('camera-mirrored', mirrored);
        }
        const mirrorBtn = document.querySelector('#btn-mirror-camera');
        if (mirrorBtn) {
            mirrorBtn.setAttribute('aria-pressed', mirrored ? 'true' : 'false');
            mirrorBtn.title = mirrored
                ? t('Tắt lật camera', 'Turn off camera mirror')
                : t('Bật lật camera', 'Turn on camera mirror');
            const label = mirrorBtn.querySelector('.mirror-label');
            if (label) {
                label.textContent = mirrored
                    ? t('Lật camera: Bật', 'Mirror: On')
                    : t('Lật camera: Tắt', 'Mirror: Off');
            }
        }
    }

    function normalizeScannedText(raw) {
        if (!raw) return '';
        let text = String(raw).trim();

        // 1. JSON payload support: {"code":"TK-..."}
        if (text.startsWith('{') && text.endsWith('}')) {
            try {
                const parsed = JSON.parse(text);
                const extracted = parsed.code || parsed.ticketCode || parsed.id;
                if (extracted) return String(extracted).trim().toUpperCase();
            } catch (e) {}
        }

        // 2. URL payload support
        try {
            if (text.includes('://')) {
                const url = new URL(text);
                const queryCode = url.searchParams.get('code') || url.searchParams.get('ticketCode') || url.searchParams.get('id');
                if (queryCode) return queryCode.trim().toUpperCase();
                const segments = url.pathname.split('/').filter(Boolean);
                const last = segments[segments.length - 1];
                if (last && /^TK-[A-Z0-9]+$/i.test(last)) return last.toUpperCase();
            }
        } catch (e) {}

        // 3. Regex match for standard BusGo ticket format (TK-XXXX)
        const match = text.match(/(TK-[A-Z0-9]+)/i);
        if (match) return match[1].toUpperCase();

        return text.toUpperCase();
    }

    async function processCheckIn(rawCode) {
        const code = normalizeScannedText(rawCode);
        if (!code) {
            showFeedback('Invalid', t('Mã vé không hợp lệ', 'Invalid Ticket Code'), t('Mã quét được để trống hoặc sai định dạng.', 'The scanned code is empty or improperly formatted.'));
            playErrorSound();
            return;
        }

        // 3-second debounce on the exact same code
        const now = Date.now();
        if (code === lastScannedCode && (now - lastScannedTime) < 3000) {
            return;
        }
        lastScannedCode = code;
        lastScannedTime = now;

        // Pause scanner during result display
        isPausedForFeedback = true;
        if (html5QrCode && isScanning) {
            try { html5QrCode.pause(true); } catch (e) {}
        }

        // Prepare anti-forgery token
        const tokenInput = document.querySelector('input[name="__RequestVerificationToken"]');
        const token = tokenInput ? tokenInput.value : '';

        const formData = new URLSearchParams();
        formData.append('code', code);
        if (token) formData.append('__RequestVerificationToken', token);

        try {
            const response = await fetch('/Admin/CheckIn?handler=Scan', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'X-Requested-With': 'XMLHttpRequest'
                },
                body: formData.toString()
            });

            if (response.status === 401 || response.status === 403) {
                showFeedback('Invalid', t('Quyền truy cập bị từ chối', 'Access Denied'), t('Tài khoản của bạn không có quyền thực hiện soát vé.', 'Your account is not permitted to perform boarding check-in.'));
                playErrorSound();
                return;
            }

            const data = await response.json();
            handleScanOutcome(data);
        } catch (err) {
            showFeedback('Invalid', t('Lỗi kết nối máy chủ', 'Server Connection Error'), t('Không thể gửi yêu cầu soát vé. Vui lòng thử lại.', 'Could not send check-in request. Please try again.'));
            playErrorSound();
        } finally {
            // Auto-resume scanner after ~2.6 seconds
            setTimeout(() => {
                isPausedForFeedback = false;
                if (html5QrCode && isScanning) {
                    try { html5QrCode.resume(); } catch (e) {}
                }
                const manualInput = document.querySelector('#manual-code-input');
                if (manualInput) {
                    manualInput.value = '';
                    manualInput.focus();
                }
            }, 2600);
        }
    }

    function handleScanOutcome(data) {
        // data.outcome: 0 = Success, 1 = AlreadyUsed, 2 = Invalid
        const outcome = typeof data.outcome === 'number'
            ? (data.outcome === 0 ? 'Success' : data.outcome === 1 ? 'AlreadyUsed' : 'Invalid')
            : data.outcome;

        if (outcome === 'Success') {
            playSuccessSound();
            showFeedback('Success', t('Hợp lệ – Đã soát vé lên xe', 'Valid – Boarding Check-in Confirmed'), data.message, data);
        } else if (outcome === 'AlreadyUsed') {
            playWarningSound();
            showFeedback('AlreadyUsed', t('Vé đã được soát trước đó', 'Ticket Already Checked In'), data.message, data);
        } else {
            playErrorSound();
            showFeedback('Invalid', t('Vé không hợp lệ', 'Invalid Ticket'), data.message || data.errorReason || t('Không thể soát vé này.', 'This ticket cannot be checked in.'), data);
        }
    }

    function showFeedback(type, title, message, data = null) {
        const banner = document.querySelector('#scan-result-banner');
        if (!banner) return;

        banner.className = 'scan-banner scan-' + type.toLowerCase();
        banner.hidden = false;

        const titleEl = banner.querySelector('.scan-banner-title');
        const msgEl = banner.querySelector('.scan-banner-msg');
        const detailsEl = banner.querySelector('.scan-banner-details');

        if (titleEl) titleEl.textContent = title;
        if (msgEl) msgEl.textContent = message;

        if (detailsEl) {
            if (data && (data.passengerName || data.route || data.ticketCode)) {
                detailsEl.innerHTML = `
                    <div class="scan-details-grid">
                        <div><small>${t('Mã vé:', 'Ticket Code:')}</small> <strong>${data.ticketCode || ''}</strong></div>
                        <div><small>${t('Hành khách:', 'Passenger:')}</small> <strong>${data.passengerName || t('Khách vãng lai', 'Walk-in Passenger')}</strong></div>
                        <div><small>${t('Chỗ ngồi:', 'Seats:')}</small> <strong>${data.seatNumbers || t('Chưa gán', 'Unassigned')}</strong></div>
                        <div><small>${t('Hành trình:', 'Journey:')}</small> <strong>${data.route || ''}</strong></div>
                        <div><small>${t('Giờ xuất bến:', 'Departure:')}</small> <strong>${data.departureTime || ''}</strong></div>
                        ${data.checkedInAt ? `<div><small>${t('Thời gian soát:', 'Checked in at:')}</small> <strong>${data.checkedInAt}</strong></div>` : ''}
                    </div>
                `;
                detailsEl.hidden = false;
            } else {
                detailsEl.hidden = true;
            }
        }

        clearTimeout(dismissTimer);
        dismissTimer = setTimeout(() => {
            banner.hidden = true;
        }, 3200);
    }

    async function startCamera() {
        const scannerPanel = document.querySelector('#camera-scanner-panel');
        const startBtn = document.querySelector('#btn-start-camera');
        const stopBtn = document.querySelector('#btn-stop-camera');
        const mirrorBtn = document.querySelector('#btn-mirror-camera');
        const cameraSelect = document.querySelector('#camera-select');
        const errorEl = document.querySelector('#camera-error-message');

        if (errorEl) errorEl.hidden = true;
        if (scannerPanel) scannerPanel.hidden = false;

        try {
            if (!window.Html5Qrcode) {
                throw new Error(t('Thư viện quét mã chưa được tải. Hãy kiểm tra kết nối mạng.', 'Scanner library failed to load. Please check your internet connection.'));
            }

            if (!html5QrCode) {
                html5QrCode = new Html5Qrcode('qr-reader');
            }

            // Enumerate devices
            const devices = await Html5Qrcode.getCameras();
            if (!devices || devices.length === 0) {
                showCameraError(t('Không tìm thấy thiết bị máy ảnh (camera) nào trên máy tính của bạn.', 'No camera device found on this system.'));
                return;
            }

            // Populate selector if multiple cameras
            if (devices.length > 1 && cameraSelect) {
                cameraSelect.innerHTML = '';
                devices.forEach((dev, idx) => {
                    const opt = document.createElement('option');
                    opt.value = dev.id;
                    opt.textContent = dev.label || `${t('Máy ảnh', 'Camera')} ${idx + 1}`;
                    cameraSelect.appendChild(opt);
                });
                cameraSelect.style.display = 'inline-block';
                cameraSelect.value = currentCameraId || devices[0].id;
                currentCameraId = cameraSelect.value;
            }

            const cameraId = currentCameraId || (devices[0] ? devices[0].id : null);
            const cameraConfig = cameraId ? { deviceId: { exact: cameraId } } : { facingMode: 'environment' };

            const scanConfig = {
                fps: 15,
                qrbox: { width: 250, height: 250 },
                aspectRatio: 1.0,
                formatsToSupport: [
                    Html5QrcodeSupportedFormats.QR_CODE,
                    Html5QrcodeSupportedFormats.CODE_128,
                    Html5QrcodeSupportedFormats.CODE_39,
                    Html5QrcodeSupportedFormats.EAN_13
                ]
            };

            await html5QrCode.start(
                cameraConfig,
                scanConfig,
                (decodedText) => {
                    if (isPausedForFeedback) return;
                    processCheckIn(decodedText);
                },
                () => {} // Silent ignore of non-detect frames
            );

            isScanning = true;
            if (startBtn) startBtn.style.display = 'none';
            if (stopBtn) stopBtn.style.display = 'inline-flex';
            if (mirrorBtn) {
                mirrorBtn.style.display = 'inline-flex';
                applyMirrorState(isMirrored);
            }
        } catch (err) {
            handleCameraError(err);
        }
    }

    async function stopCamera() {
        const scannerPanel = document.querySelector('#camera-scanner-panel');
        const startBtn = document.querySelector('#btn-start-camera');
        const stopBtn = document.querySelector('#btn-stop-camera');
        const mirrorBtn = document.querySelector('#btn-mirror-camera');

        if (html5QrCode && isScanning) {
            try {
                await html5QrCode.stop();
            } catch (e) {}
            isScanning = false;
        }

        if (scannerPanel) scannerPanel.hidden = true;
        if (startBtn) startBtn.style.display = 'inline-flex';
        if (stopBtn) stopBtn.style.display = 'none';
        if (mirrorBtn) mirrorBtn.style.display = 'none';
    }

    function handleCameraError(err) {
        const errStr = (err ? err.message || err.toString() : '').toLowerCase();
        let msg = t('Không thể khởi động máy ảnh.', 'Could not start camera.');

        if (errStr.includes('notallowed') || errStr.includes('permission') || errStr.includes('denied')) {
            msg = t(
                'Quyền truy cập máy ảnh bị từ chối. Vui lòng bấm vào biểu tượng ổ khóa 🔒 trên thanh địa chỉ của trình duyệt, bật cho phép Máy ảnh (Camera) rồi tải lại trang.',
                'Camera permission denied. Please click the lock icon 🔒 in your browser address bar, enable Camera access, and refresh the page.'
            );
        } else if (errStr.includes('notfound') || errStr.includes('device') || errStr.includes('devicesnotfound')) {
            msg = t('Không tìm thấy thiết bị máy ảnh trên máy tính này.', 'No camera device found on this system.');
        } else if (errStr.includes('notreadable') || errStr.includes('in use') || errStr.includes('busy')) {
            msg = t(
                'Máy ảnh đang bận hoặc đang được sử dụng bởi ứng dụng khác (Zalo, Zoom, Teams, Meet). Vui lòng tắt ứng dụng kia và thử lại.',
                'Camera is in use by another application (Zoom, Teams, Meet). Please close that app and try again.'
            );
        } else {
            msg = t('Lỗi máy ảnh: ', 'Camera error: ') + (err.message || err);
        }

        showCameraError(msg);
    }

    function showCameraError(msg) {
        const errorEl = document.querySelector('#camera-error-message');
        if (errorEl) {
            errorEl.textContent = msg;
            errorEl.hidden = false;
        }
        stopCamera();
    }

    document.addEventListener('DOMContentLoaded', () => {
        const startBtn = document.querySelector('#btn-start-camera');
        const stopBtn = document.querySelector('#btn-stop-camera');
        const mirrorBtn = document.querySelector('#btn-mirror-camera');
        const cameraSelect = document.querySelector('#camera-select');
        const soundBtn = document.querySelector('#btn-sound-toggle');
        const manualInput = document.querySelector('#manual-code-input');
        const manualForm = document.querySelector('#manual-checkin-form');

        // Initial mirror state
        if (mirrorBtn) {
            applyMirrorState(isMirrored);
            mirrorBtn.addEventListener('click', () => {
                applyMirrorState(!isMirrored);
            });
        }

        // Initial sound state
        if (soundBtn) {
            soundBtn.setAttribute('aria-pressed', isMuted ? 'false' : 'true');
            soundBtn.title = isMuted ? t('Bật âm thanh báo', 'Turn sound on') : t('Tắt âm thanh báo', 'Turn sound off');
            soundBtn.querySelector('.sound-label').textContent = isMuted ? t('Âm thanh: Tắt', 'Sound: Off') : t('Âm thanh: Bật', 'Sound: On');

            soundBtn.addEventListener('click', () => {
                isMuted = !isMuted;
                localStorage.setItem('busgo_checkin_muted', String(isMuted));
                soundBtn.setAttribute('aria-pressed', isMuted ? 'false' : 'true');
                soundBtn.title = isMuted ? t('Bật âm thanh báo', 'Turn sound on') : t('Tắt âm thanh báo', 'Turn sound off');
                soundBtn.querySelector('.sound-label').textContent = isMuted ? t('Âm thanh: Tắt', 'Sound: Off') : t('Âm thanh: Bật', 'Sound: On');
                if (!isMuted) playSuccessSound();
            });
        }

        startBtn?.addEventListener('click', startCamera);
        stopBtn?.addEventListener('click', stopCamera);

        cameraSelect?.addEventListener('change', async function () {
            currentCameraId = this.value;
            if (isScanning) {
                await stopCamera();
                await startCamera();
            }
        });

        // Manual input Enter key integration (also handles handheld USB laser barcode scanners)
        if (manualInput) {
            manualInput.focus();
            manualInput.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') {
                    e.preventDefault();
                    const val = manualInput.value.trim();
                    if (val) processCheckIn(val);
                }
            });
        }

        if (manualForm) {
            manualForm.addEventListener('submit', (e) => {
                e.preventDefault();
                const val = manualInput ? manualInput.value.trim() : '';
                if (val) processCheckIn(val);
            });
        }

        // Clean up camera on leave
        window.addEventListener('beforeunload', stopCamera);
        window.addEventListener('pagehide', stopCamera);
    });
})();
