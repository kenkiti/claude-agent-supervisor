(function () {
    'use strict';

    const root = document.documentElement;
    const themeButton = document.getElementById('theme-toggle');
    const themeKey = 'agentsupervisor-theme';

    function updateThemeButton() {
        const theme = root.dataset.theme || 'system';
        themeButton.textContent = theme === 'dark' ? '☀' : '☾';
        themeButton.setAttribute('aria-label', theme === 'dark' ? '明るい配色に切り替え' : '暗い配色に切り替え');
        themeButton.title = theme === 'system' ? 'OS設定の配色' : theme === 'dark' ? '暗い配色' : '明るい配色';
    }
    if (themeButton) {
        updateThemeButton();
        themeButton.addEventListener('click', function () {
            const current = root.dataset.theme || 'system';
            const next = current === 'system' ? 'light' : current === 'light' ? 'dark' : 'system';
            if (next === 'system') { delete root.dataset.theme; localStorage.removeItem(themeKey); }
            else { root.dataset.theme = next; localStorage.setItem(themeKey, next); }
            updateThemeButton();
        });
    }

    function renderStatusPill(container, label, statusClass) {
        const pill = document.createElement('span');
        pill.className = 'status-pill status-' + statusClass;
        const dot = document.createElement('span');
        dot.className = 'status-dot';
        pill.appendChild(dot);
        const text = document.createTextNode(String(label ?? ''));
        pill.appendChild(text);
        if (container) container.appendChild(pill);
        return pill;
    }
    function sessionStatusClass(value) { return value === 'running' ? 'good' : value === 'stopped' ? 'warning' : 'neutral'; }
    function taskStatusClass(value) { return value === 'running' || value === 'succeeded' ? 'good' : value === 'retrying' ? 'warning' : value === 'failed' || value === 'exhausted' || value === 'budget-exceeded' ? 'critical' : 'neutral'; }
    function runtimeBadge(value) { const badge = document.createElement('span'); badge.className = 'badge-runtime'; badge.dataset.runtime = String(value ?? ''); badge.textContent = String(value ?? ''); return badge; }

    function setConnectionStatus(label, statusClass) {
        const status = document.getElementById('connection-status');
        if (!status) return;
        while (status.firstChild) status.removeChild(status.firstChild);
        status.className = 'status-pill status-' + statusClass;
        const dot = document.createElement('span');
        dot.className = 'status-dot';
        status.appendChild(dot);
        status.appendChild(document.createTextNode(label));
    }

    function formatTimestamp(value) {
        if (!value) return '';
        const d = new Date(value);
        if (Number.isNaN(d.getTime())) return String(value);
        const pad = function (n) { return String(n).padStart(2, '0'); };
        return d.getUTCFullYear() + '/' + pad(d.getUTCMonth() + 1) + '/' + pad(d.getUTCDate()) + ' ' +
            pad(d.getUTCHours()) + ':' + pad(d.getUTCMinutes()) + ':' + pad(d.getUTCSeconds()) + ' +00:00';
    }

    function cell(row, value, className) { const td = document.createElement('td'); if (className) td.className = className; td.textContent = String(value ?? ''); row.appendChild(td); return td; }
    function dateCell(row, value) { return cell(row, formatTimestamp(value)); }
    function statusCell(row, value, statusClass) { const td = cell(row, '', ''); td.dataset.status = String(value ?? ''); renderStatusPill(td, value, statusClass); return td; }
    function rebuildRows(sessions, observeOnly) {
        const sessionBody = document.getElementById('sessions-body');
        const observeBody = document.getElementById('observe-only-body');
        if (sessionBody) {
            while (sessionBody.firstChild) sessionBody.removeChild(sessionBody.firstChild);
            sessions.forEach(function (item) { const row = document.createElement('tr'); cell(row, item.project); const runtime = document.createElement('td'); runtime.appendChild(runtimeBadge(item.runtime)); row.appendChild(runtime); statusCell(row, item.lifecycleState, sessionStatusClass(item.lifecycleState)); dateCell(row, item.updatedAt); cell(row, item.source); cell(row, item.confidence); sessionBody.appendChild(row); });
        }
        if (observeBody) {
            while (observeBody.firstChild) observeBody.removeChild(observeBody.firstChild);
            observeOnly.forEach(function (item) { const row = document.createElement('tr'); cell(row, item.sessionId); const runtime = document.createElement('td'); runtime.appendChild(runtimeBadge(item.runtimeId)); row.appendChild(runtime); cell(row, item.lastEventName); dateCell(row, item.occurredAt); observeBody.appendChild(row); });
        }
    }
    async function refreshSessions() { const responses = await Promise.all([fetch('/api/v1/sessions'), fetch('/api/v1/sessions/observe-only')]); if (!responses[0].ok || !responses[1].ok) throw new Error('Session refresh failed'); rebuildRows(await responses[0].json(), await responses[1].json()); }

    if (typeof signalR !== 'undefined') {
        const connection = new signalR.HubConnectionBuilder().withUrl('/hubs/sessions').withAutomaticReconnect([0, 2000, 5000, 10000, 15000, 30000]).build();
        connection.onreconnecting(function () { setConnectionStatus('再接続中', 'warning'); });
        connection.onreconnected(function () { setConnectionStatus('接続中', 'good'); });
        connection.onclose(function () { setConnectionStatus('切断', 'critical'); });
        let refreshTimer;
        connection.on('sessionsUpdated', function () { if (document.querySelector('main[data-page="sessions"]')) { clearTimeout(refreshTimer); refreshTimer = setTimeout(function () { refreshSessions().catch(function (error) { console.error('Session refresh failed', error); }); }, 300); } });
        connection.start().then(function () { setConnectionStatus('接続中', 'good'); }).catch(function (error) { setConnectionStatus('切断', 'critical'); console.error('SignalR connection failed', error); });
    }
    if (document.querySelector('main[data-page="settings"]')) {
        document.querySelectorAll('.settings-card[data-channel]').forEach(function (card) {
            const channel = card.dataset.channel;
            const input = document.getElementById(channel + '-webhook-input');
            const saveButton = document.getElementById(channel + '-save');
            const testButton = document.getElementById(channel + '-test');
        const message = document.getElementById(channel + '-message');
        const toggleButton = document.getElementById(channel + '-toggle');

        if (toggleButton) {
            toggleButton.addEventListener('click', async () => {
                const current = toggleButton.dataset.enabled === 'true';
                toggleButton.disabled = true;
                try {
                    const response = await fetch('/api/v1/notifications/channels/' + channel + '/enabled', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ enabled: !current })
                    });
                    if (!response.ok) throw new Error('toggle failed');
                    const enabled = !current;
                    toggleButton.dataset.enabled = enabled ? 'true' : 'false';
                    toggleButton.setAttribute('aria-pressed', enabled ? 'true' : 'false');
                    toggleButton.textContent = enabled ? '通知ON' : '通知OFF';
                    showMessage(enabled ? '通知をONにしました' : '通知をOFFにしました', 'good');
                } catch {
                    showMessage('切り替えに失敗しました', 'critical');
                } finally {
                    toggleButton.disabled = false;
                }
            });
        }
            const status = document.getElementById(channel + '-status');
            const statusLabel = document.getElementById(channel + '-status-label');
            const suffix = document.getElementById(channel + '-suffix');
            const visibilityButton = card.querySelector('[data-action="toggle-visibility"]');

            function isValidUrl(value) {
                try { return value.trim() !== '' && new URL(value.trim()).protocol === 'https:'; }
                catch (_) { return false; }
            }
            function showMessage(label, statusClass) {
                while (message.firstChild) message.removeChild(message.firstChild);
                renderStatusPill(message, label, statusClass);
            }
            function setConfigured(configured, maskedSuffix) {
                status.className = 'status-pill status-' + (configured ? 'good' : 'neutral');
                while (status.firstChild) status.removeChild(status.firstChild);
                const dot = document.createElement('span');
                dot.className = 'status-dot';
                status.appendChild(dot);
                statusLabel.textContent = configured ? '設定済み' : '未設定';
                status.appendChild(statusLabel);
                suffix.textContent = maskedSuffix || '';
                suffix.hidden = !maskedSuffix;
                testButton.disabled = !configured;
            }
            function updateSaveState() { saveButton.disabled = !isValidUrl(input.value); }
            function responseReason(response, fallback) { return response.status ? 'HTTP ' + response.status : fallback; }

            visibilityButton.addEventListener('click', function () {
                const visible = input.type === 'text';
                input.type = visible ? 'password' : 'text';
                visibilityButton.textContent = visible ? '◉' : '○';
                visibilityButton.setAttribute('aria-pressed', String(!visible));
                visibilityButton.setAttribute('aria-label', (visible ? channel + ' Webhook URLを表示' : channel + ' Webhook URLを隠す'));
            });
            input.addEventListener('input', updateSaveState);
            saveButton.addEventListener('click', function () {
                const value = input.value.trim();
                if (!isValidUrl(value)) { showMessage('保存に失敗しました（HTTPS URLを入力してください）', 'critical'); updateSaveState(); return; }
                saveButton.disabled = true;
                saveButton.textContent = '保存中…';
                fetch('/api/v1/notifications/channels/' + channel, { method: 'POST', headers: { 'Content-Type': 'text/plain' }, body: value })
                    .then(async function (response) { if (!response.ok) throw new Error(responseReason(response, '保存に失敗しました')); return response.json(); })
                    .then(function (result) { input.value = ''; setConfigured(Boolean(result.configured), result.maskedSuffix); showMessage('保存しました', 'good'); })
                    .catch(function (error) { showMessage('保存に失敗しました（' + (error.message || '通信エラー') + '）', 'critical'); })
                    .finally(function () { saveButton.textContent = '保存'; updateSaveState(); });
            });
            testButton.addEventListener('click', function () {
                if (testButton.disabled) return;
                testButton.disabled = true;
                testButton.textContent = '送信中…';
                fetch('/api/v1/notifications/test/' + channel, { method: 'POST' })
                    .then(function (response) { if (!response.ok) throw new Error(responseReason(response, 'テスト送信に失敗しました')); showMessage('テスト通知を送信しました', 'good'); })
                    .catch(function (error) { showMessage('テスト送信に失敗しました（' + (error.message || '通信エラー') + '）', 'critical'); })
                    .finally(function () { testButton.textContent = 'テスト送信'; testButton.disabled = !status.classList.contains('status-good'); });
            });
            updateSaveState();
        });
        const exitOnAuthFailureToggle = document.getElementById('exit-on-auth-failure-toggle');
        if (exitOnAuthFailureToggle) {
            exitOnAuthFailureToggle.addEventListener('click', async () => {
                const current = exitOnAuthFailureToggle.dataset.enabled === 'true';
                exitOnAuthFailureToggle.disabled = true;
                try {
                    const response = await fetch('/api/v1/settings/exit-on-auth-failure', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ enabled: !current })
                    });
                    if (!response.ok) throw new Error('toggle failed');
                    const enabled = !current;
                    exitOnAuthFailureToggle.dataset.enabled = enabled ? 'true' : 'false';
                    exitOnAuthFailureToggle.setAttribute('aria-pressed', enabled ? 'true' : 'false');
                    exitOnAuthFailureToggle.textContent = '認証失敗時にアプリを自動終了する: ' + (enabled ? 'ON' : 'OFF');
                } catch {
                } finally {
                    exitOnAuthFailureToggle.disabled = false;
                }
            });
        }
    }
})();
