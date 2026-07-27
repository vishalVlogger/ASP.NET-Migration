document.querySelectorAll('.source-tab').forEach(button => {
    button.addEventListener('click', () => {
        document.querySelectorAll('.source-tab').forEach(x => x.classList.remove('active'));
        document.querySelectorAll('.source-pane').forEach(x => x.classList.remove('active'));
        button.classList.add('active');
        document.querySelector(`[data-pane="${button.dataset.tab}"]`).classList.add('active');
    });
});

const fileInput = document.querySelector('#Files');
const dropzone = document.querySelector('.dropzone');
const fileList = document.querySelector('#file-list');
function renderFiles() {
    if (!fileInput || !fileList) return;
    fileList.innerHTML = [...fileInput.files].map(file =>
        `<div class="file-chip"><span>${escapeHtml(file.name)}</span><span>${formatBytes(file.size)}</span></div>`
    ).join('');
}
fileInput?.addEventListener('change', renderFiles);
['dragenter', 'dragover'].forEach(name => dropzone?.addEventListener(name, event => { event.preventDefault(); dropzone.classList.add('dragging'); }));
['dragleave', 'drop'].forEach(name => dropzone?.addEventListener(name, event => { event.preventDefault(); dropzone.classList.remove('dragging'); }));
dropzone?.addEventListener('drop', event => { fileInput.files = event.dataTransfer.files; renderFiles(); });

const migrationForm = document.querySelector('#migration-form');
migrationForm?.addEventListener('submit', async event => {
    if (!window.fetch) return;
    event.preventDefault();
    if (!migrationForm.checkValidity() || (window.jQuery && !window.jQuery(migrationForm).valid())) {
        migrationForm.reportValidity();
        return;
    }

    const button = document.querySelector('#migrate-button');
    const progressPanel = document.querySelector('#migration-progress');
    button.classList.add('loading');
    button.querySelector('.button-label').textContent = 'Migrating…';
    progressPanel.hidden = false;
    progressPanel.classList.remove('failed');
    document.querySelector('#progress-error').hidden = true;
    updateProgress(4, 'Uploading and validating source');

    try {
        const response = await fetch(migrationForm.dataset.startUrl, {
            method: 'POST', body: new FormData(migrationForm), headers: { 'X-Requested-With': 'XMLHttpRequest' }
        });
        const payload = await response.json();
        if (!response.ok) throw new Error(payload.errors?.join(' ') || 'The migration could not be started.');
        await pollMigration(payload.jobId);
    } catch (error) {
        showProgressError(error.message || 'The migration could not be completed.');
    }
});

async function pollMigration(jobId) {
    while (true) {
        const response = await fetch(`${migrationForm.dataset.statusUrl}?id=${encodeURIComponent(jobId)}`, { cache: 'no-store' });
        if (!response.ok) throw new Error('Migration status is no longer available.');
        const job = await response.json();
        updateProgress(job.percent, job.stage);
        if (['failed', 'interrupted', 'cancelled'].includes(job.state)) {
            throw new Error(`${job.error || job.stage || 'Migration paused.'} Open the migration dashboard to resume.`);
        }
        if (job.state === 'complete') {
            await new Promise(resolve => setTimeout(resolve, 650));
            window.location.assign(job.resultUrl);
            return;
        }
        if (job.state === 'needs-review') {
            await new Promise(resolve => setTimeout(resolve, 650));
            window.location.assign(job.resultUrl);
            return;
        }
        await new Promise(resolve => setTimeout(resolve, 500));
    }
}

function updateProgress(percent, stage) {
    const safePercent = Math.max(0, Math.min(100, Number(percent) || 0));
    document.querySelector('#progress-fill').style.width = `${safePercent}%`;
    document.querySelector('#progress-percent').textContent = `${safePercent}%`;
    document.querySelector('#progress-stage').textContent = stage;
    const track = document.querySelector('.progress-track');
    track.setAttribute('aria-valuenow', safePercent);
    track.setAttribute('aria-valuetext', stage);
    document.querySelectorAll('.progress-stages span').forEach(marker => {
        const threshold = Number(marker.dataset.threshold);
        marker.classList.toggle('complete', safePercent >= threshold);
        marker.classList.toggle('active', safePercent < threshold && safePercent >= threshold - 20);
    });
}

function showProgressError(message) {
    const panel = document.querySelector('#migration-progress');
    const error = document.querySelector('#progress-error');
    panel.classList.add('failed');
    error.textContent = message;
    error.hidden = false;
    document.querySelector('#progress-stage').textContent = 'Migration stopped';
    const button = document.querySelector('#migrate-button');
    button.classList.remove('loading');
    button.querySelector('.button-label').textContent = 'Try migration again';
}

document.querySelectorAll('.file-tab').forEach(button => button.addEventListener('click', () => {
    document.querySelectorAll('.file-tab').forEach(x => x.classList.remove('active'));
    document.querySelectorAll('.code-file').forEach(x => x.classList.remove('active'));
    button.classList.add('active');
    document.querySelector(`#${button.dataset.file}`).classList.add('active');
}));

document.querySelector('#generated-file-picker')?.addEventListener('change', event => {
    const current = document.querySelector('.code-file.active.dirty');
    if (current && !window.confirm('Discard unsaved edits and open another file?')) {
        event.target.value = current.id;
        return;
    }
    document.querySelectorAll('.code-file').forEach(file => file.classList.remove('active'));
    document.querySelector(`#${event.target.value}`)?.classList.add('active');
});

document.querySelectorAll('.diagnostic').forEach(button => button.addEventListener('click', () => {
    const picker = document.querySelector('#generated-file-picker');
    if (!picker) return;
    const diagnosticPath = (button.dataset.diagnosticFile || '').replaceAll('\\', '/').toLowerCase();
    const option = [...picker.options].find(item => item.text.replaceAll('\\', '/').toLowerCase().endsWith(diagnosticPath));
    if (!option) return;
    picker.value = option.value;
    picker.dispatchEvent(new Event('change'));
    picker.scrollIntoView({ behavior: 'smooth', block: 'center' });
}));

document.querySelectorAll('.copy-button').forEach(button => button.addEventListener('click', async () => {
    const text = button.closest('.code-file').querySelector('.code-editor').value;
    await navigator.clipboard.writeText(text);
    button.textContent = 'Copied';
    setTimeout(() => button.textContent = 'Copy code', 1200);
}));

document.querySelectorAll('.code-editor').forEach(editor => {
    editor.addEventListener('input', () => editor.closest('.code-file').classList.add('dirty'));
    editor.addEventListener('keydown', event => {
        if (event.key !== 'Tab') return;
        event.preventDefault();
        const start = editor.selectionStart;
        editor.setRangeText('    ', start, editor.selectionEnd, 'end');
        editor.closest('.code-file').classList.add('dirty');
    });
});

document.querySelectorAll('.save-button').forEach(button => button.addEventListener('click', async () => {
    const file = button.closest('.code-file');
    await runEditorAction(button, document.querySelector('#results').dataset.saveUrl, {
        path: file.querySelector('.code-editor').dataset.path,
        content: file.querySelector('.code-editor').value
    }, 'Saving and rebuilding…');
}));

document.querySelectorAll('.regenerate-button').forEach(button => button.addEventListener('click', async () => {
    if (!window.confirm('Regenerate this file? Unsaved edits in this file will be replaced.')) return;
    const file = button.closest('.code-file');
    await runEditorAction(button, document.querySelector('#results').dataset.regenerateUrl, {
        path: file.querySelector('.code-editor').dataset.path
    }, 'Regenerating selected file…');
}));

async function runEditorAction(button, url, values, workingMessage) {
    const results = document.querySelector('#results');
    const file = button.closest('.code-file');
    const status = file.querySelector('.editor-status');
    const originalLabel = button.textContent;
    button.disabled = true;
    status.className = 'editor-status active';
    status.textContent = workingMessage;
    const form = new FormData();
    form.append('__RequestVerificationToken', document.querySelector('input[name="__RequestVerificationToken"]').value);
    form.append('resultId', results.dataset.resultId);
    Object.entries(values).forEach(([key, value]) => form.append(key, value));
    try {
        const response = await fetch(url, { method: 'POST', body: form, headers: { 'X-Requested-With': 'XMLHttpRequest' } });
        const payload = await response.json();
        if (!response.ok) throw new Error(payload.error || 'The file operation failed.');
        status.textContent = `${payload.summary} Refreshing verification report…`;
        file.classList.remove('dirty');
        setTimeout(() => window.location.reload(), 700);
    } catch (error) {
        status.className = 'editor-status error';
        status.textContent = error.message || 'The file operation failed.';
        button.disabled = false;
        button.textContent = originalLabel;
    }
}

document.querySelector('.copy-tree')?.addEventListener('click', async event => {
    await navigator.clipboard.writeText(document.querySelector('#project-tree-code').textContent);
    event.currentTarget.textContent = 'Tree copied';
    setTimeout(() => event.currentTarget.textContent = 'Copy tree', 1200);
});

function escapeHtml(value) { const div = document.createElement('div'); div.textContent = value; return div.innerHTML; }
function formatBytes(bytes) { return bytes < 1024 ? `${bytes} B` : `${(bytes / 1024).toFixed(1)} KB`; }

if (document.querySelector('#results')) document.querySelector('#results').scrollIntoView({ behavior: 'smooth', block: 'start' });
