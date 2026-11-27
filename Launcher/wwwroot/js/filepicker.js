// In-app filesystem browser. It avoids native/HTML file dialogs, which some
// embedded WebView hosts (notably Wine-based GameNative sessions) do not expose.
var _filePickerMode = 'folder';
var _filePickerSelectedPath = '';
var _filePickerParentPath = null;
var _filePickerCurrentPath = '';
var _filePickerBusy = false;
var _filePickerRequestTimer = null;

function openFilePicker(mode) {
    _filePickerMode = mode === 'image' ? 'image' : 'folder';
    _filePickerSelectedPath = '';
    var overlay = document.getElementById('filePickerOverlay');
    var title = document.getElementById('filePickerTitle');
    var useButton = document.getElementById('filePickerUseBtn');
    title.textContent = _filePickerMode === 'image' ? 'Choose server image' : 'Choose game installation folder';
    useButton.textContent = _filePickerMode === 'image' ? 'Use image' : 'Use this folder';
    useButton.disabled = true;
    overlay.style.display = 'flex';
    requestFilePickerListing('');
}

function requestFilePickerListing(path) {
    if (_filePickerBusy) return;
    _filePickerBusy = true;
    _filePickerSelectedPath = '';
    updateFilePickerSelection();
    var list = document.getElementById('filePickerEntries');
    list.innerHTML = '<div class="file-picker-empty">Loading folders…</div>';
    document.getElementById('filePickerStatus').textContent = 'Reading the selected location…';
    if (_filePickerRequestTimer) clearTimeout(_filePickerRequestTimer);
    _filePickerRequestTimer = setTimeout(function() {
        if (!_filePickerBusy) return;
        _filePickerBusy = false;
        list.innerHTML = '<div class="file-picker-empty">The launcher did not respond. Close the picker and try again.</div>';
        document.getElementById('filePickerStatus').textContent = 'Filesystem request timed out.';
    }, 15000);
    try {
        send('browseFileSystem', { mode: _filePickerMode, path: path || '' });
    } catch (error) {
        _filePickerBusy = false;
        clearTimeout(_filePickerRequestTimer);
        list.innerHTML = '<div class="file-picker-empty">Could not contact the launcher.</div>';
        document.getElementById('filePickerStatus').textContent = error && error.message ? error.message : 'Request failed.';
    }
}

function onFileSystemListing(data) {
    _filePickerBusy = false;
    if (_filePickerRequestTimer) {
        clearTimeout(_filePickerRequestTimer);
        _filePickerRequestTimer = null;
    }
    if (!data || data.type !== 'fileSystemListing') return;
    if (data.mode !== _filePickerMode && _filePickerMode) return;
    _filePickerCurrentPath = data.path || '';
    _filePickerParentPath = data.parent || null;
    document.getElementById('filePickerPath').value = _filePickerCurrentPath;
    document.getElementById('filePickerUpBtn').disabled = !_filePickerParentPath && !_filePickerCurrentPath;
    var entries = document.getElementById('filePickerEntries');
    entries.replaceChildren();
    _filePickerSelectedPath = '';
    updateFilePickerSelection();

    if (!data.ok) {
        entries.innerHTML = '<div class="file-picker-empty">' + escapeHtml(data.error || 'Could not read this location.') + '</div>';
        document.getElementById('filePickerUseBtn').disabled = true;
        document.getElementById('filePickerStatus').textContent = 'Try another folder or drive.';
        return;
    }

    var items = data.entries || [];
    if (!items.length) {
        var empty = document.createElement('div');
        empty.className = 'file-picker-empty';
        empty.textContent = data.path ? (_filePickerMode === 'image' ? 'No folders or supported images here.' : 'No subfolders found here.') : 'No accessible drives were found.';
        entries.appendChild(empty);
    }

    items.forEach(function(item) {
        var row = document.createElement('div');
        row.className = 'file-picker-entry';
        row.dataset.path = item.path || '';
        row.dataset.directory = item.isDirectory ? '1' : '0';
        row.setAttribute('aria-selected', 'false');

        var selectButton = document.createElement('button');
        selectButton.type = 'button';
        selectButton.className = 'file-picker-entry-select';
        var icon = document.createElement('span');
        icon.className = 'file-picker-entry-icon';
        icon.textContent = item.isDirectory ? '📁' : '🖼';
        var name = document.createElement('span');
        name.className = 'file-picker-entry-name';
        name.textContent = item.name || item.path || '(unnamed)';
        selectButton.append(icon, name);
        selectButton.addEventListener('click', function() {
            _filePickerSelectedPath = item.path;
            updateFilePickerSelection();
            if (!item.isDirectory) useFilePickerSelection();
        });
        row.appendChild(selectButton);

        if (item.isDirectory) {
            var openButton = document.createElement('button');
            openButton.type = 'button';
            openButton.className = 'file-picker-entry-open';
            openButton.textContent = 'Open';
            openButton.addEventListener('click', function() { requestFilePickerListing(item.path); });
            row.appendChild(openButton);
        }
        entries.appendChild(row);
    });

    var status = data.path || 'Drives and filesystem roots';
    if (data.truncated) status += ' · showing first 500 entries';
    document.getElementById('filePickerStatus').textContent = status;
}

function updateFilePickerSelection() {
    var entries = document.getElementById('filePickerEntries');
    if (entries) {
        entries.querySelectorAll('.file-picker-entry').forEach(function(row) {
            var selected = row.dataset.path === _filePickerSelectedPath;
            row.classList.toggle('selected', selected);
            row.setAttribute('aria-selected', selected ? 'true' : 'false');
        });
    }
    var useButton = document.getElementById('filePickerUseBtn');
    if (useButton) {
        useButton.disabled = !_filePickerSelectedPath && !(_filePickerMode === 'folder' && _filePickerCurrentPath);
    }
}

function filePickerNavigateUp() {
    if (_filePickerParentPath) requestFilePickerListing(_filePickerParentPath);
    else if (_filePickerCurrentPath) requestFilePickerListing('__CYPRESS_ROOTS__');
}

function filePickerGoToPath() {
    requestFilePickerListing(document.getElementById('filePickerPath').value.trim());
}

function filePickerReload() {
    requestFilePickerListing(_filePickerCurrentPath);
}

function useFilePickerSelection() {
    var path = _filePickerMode === 'folder'
        ? (_filePickerSelectedPath || _filePickerCurrentPath)
        : _filePickerSelectedPath;
    if (!path) {
        document.getElementById('filePickerStatus').textContent = _filePickerMode === 'image' ? 'Select an image first.' : 'Select a folder first.';
        return;
    }
    closeFilePicker();
    if (_filePickerMode === 'image') send('selectServerIconPath', { path: path });
    else send('setGameDirectory', { path: path });
}

function closeFilePicker() {
    document.getElementById('filePickerOverlay').style.display = 'none';
    _filePickerBusy = false;
    if (_filePickerRequestTimer) {
        clearTimeout(_filePickerRequestTimer);
        _filePickerRequestTimer = null;
    }
}

