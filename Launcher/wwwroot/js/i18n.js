// i18n translation system
// usage: t('key') or t('key', {var: value}) for {var} interpolation
// html: add data-i18n="key" on elements whose textContent should be translated
//       add data-i18n-placeholder="key" for input placeholders
//       add data-i18n-title="key" for title attributes
//       add data-i18n-tip="key" for data-tip tooltip attributes

window._i18nStrings = {};
window._i18nLangMeta = {};

function resolveTranslation(key) {
    var raw = key == null ? '' : String(key).trim();
    if (!raw) return '';

    var strings = window._i18nStrings || {};
    var candidates = [raw, raw.toLowerCase()];

    // Older UI surfaces can supply identifiers such as
    // JOIN.SERVER_ADDRESS_LABEL while the bundled catalog uses
    // join.server_address_label. Try the normalized form before falling back.
    var normalized = raw.replace(/\s+/g, '_').toLowerCase();
    if (candidates.indexOf(normalized) === -1) candidates.push(normalized);

    for (var i = 0; i < candidates.length; i++) {
        var candidate = candidates[i];
        if (Object.prototype.hasOwnProperty.call(strings, candidate) && typeof strings[candidate] === 'string') {
            return strings[candidate];
        }
    }

    // A missing translation should still be readable; never expose the raw
    // localization token to the player.
    var label = raw.split(/[.:/]/).filter(Boolean).pop() || raw;
    label = label
        .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
        .replace(/[_-]+/g, ' ')
        .replace(/\s+/g, ' ')
        .trim();
    return label.toLowerCase().replace(/\b[a-z]/g, function(letter) { return letter.toUpperCase(); });
}

window.t = function(key, vars) {
    var str = resolveTranslation(key);
    if (vars) {
        str = str.replace(/\{(\w+)\}/g, function(_, k) {
            return vars[k] !== undefined ? String(vars[k]) : '{' + k + '}';
        });
    }
    return str;
};

function onTranslationsList(data) {
    var select = document.getElementById('languageSelect');
    if (!select || !data.langs) return;
    // migrate old underscore-format locale codes to BCP 47 (e.g. zh_hk -> zh-HK, es_419 -> es-419)
    var _storedLang = localStorage.getItem('cypress_lang');
    if (_storedLang && _storedLang.indexOf('_') !== -1) {
        var _parts = _storedLang.split('_');
        var _normalized = _parts[0].toLowerCase() + '-' + (/^\d+$/.test(_parts[1]) ? _parts[1] : _parts[1].toUpperCase());
        localStorage.setItem('cypress_lang', _normalized);
    }
    var current = localStorage.getItem('cypress_lang') || 'en-US';
    select.innerHTML = '';
    data.langs.forEach(function(item) {
        var lang   = typeof item === 'string' ? item : item.lang;
        var name   = (typeof item === 'object' && item.name)   ? item.name   : lang;
        var author = (typeof item === 'object') ? (item.author || '') : '';
        window._i18nLangMeta[lang] = { name: name, author: author };
        var opt = document.createElement('option');
        opt.value = lang;
        opt.textContent = name + ' (' + lang + ')';
        if (lang === current) opt.selected = true;
        select.appendChild(opt);
    });
    if (typeof renderPickerOptions === 'function') renderPickerOptions('languageSelect');
    _updateLangAuthorHint(current);
}

function onLanguageChanged(lang) {
    if (!lang) return;
    localStorage.setItem('cypress_lang', lang);
    send('getTranslations', { lang: lang });
    _updateLangAuthorHint(lang);
}

function _normalizeLangKey(lang) {
    if (!lang || lang.indexOf('_') === -1) return lang;
    var parts = lang.split('_');
    return parts[0].toLowerCase() + '-' + (/^\d+$/.test(parts[1]) ? parts[1] : parts[1].toUpperCase());
}

function _formatAuthor(author) {
    if (!author) return '';
    if (Array.isArray(author)) return author.join(', ');
    return String(author);
}

function _updateLangAuthorHint(lang) {
    var hint = document.getElementById('langAuthorHint');
    if (!hint) return;
    var key = _normalizeLangKey(lang);
    var cached = window._i18nLangMeta[key] || window._i18nLangMeta[lang];
    var author = _formatAuthor(cached ? cached.author : '');
    if (author) {
        hint.textContent = t('profile.translation_by') + ' ' + author;
        hint.style.display = '';
    } else {
        hint.style.display = 'none';
    }
}

function applyDomTranslations() {
    document.body.style.visibility = '';
    document.querySelectorAll('[data-i18n]').forEach(function(el) {
        var key = el.getAttribute('data-i18n');
        el.textContent = t(key);
        el.dir = 'auto';
    });
    document.querySelectorAll('[data-i18n-placeholder]').forEach(function(el) {
        var key = el.getAttribute('data-i18n-placeholder');
        el.placeholder = t(key);
    });
    document.querySelectorAll('[data-i18n-title]').forEach(function(el) {
        var key = el.getAttribute('data-i18n-title');
        el.title = t(key);
    });
    document.querySelectorAll('[data-i18n-tip]').forEach(function(el) {
        var key = el.getAttribute('data-i18n-tip');
        el.setAttribute('data-tip', t(key));
    });

    // rerender picker uis whose option elements carry data-i18n translations,
    // since renderPickerOptions runs at init before translations arrive
    // (this is a bit hacky i know)
    if (typeof renderPickerOptions === 'function') {
        var translatedSelects = new Set();
        document.querySelectorAll('select option[data-i18n]').forEach(function(opt) {
            var select = opt.closest('select');
            if (select && select.id) translatedSelects.add(select.id);
        });
        translatedSelects.forEach(function(id) { renderPickerOptions(id); });
    }
    if (typeof addPlaylistEntry === 'function') {
        var plEntries = document.getElementById('plEntries');
        if (plEntries && !plEntries.querySelector('.pl-entry')) {
            addPlaylistEntry();
        }
    }

    if (typeof updateGameLibrarySection === 'function') updateGameLibrarySection();
    document.querySelectorAll('.update-btn:not([disabled])').forEach(function(btn) {
        btn.textContent = t('updates.update_btn');
    });
    document.querySelectorAll('.update-dismiss-btn').forEach(function(btn) {
        btn.textContent = t('updates.later');
    });

    // re-render author hint with now-translated "Translation by" string
    var _loadedMeta = window._i18nStrings && window._i18nStrings['_meta'];
    if (_loadedMeta && _loadedMeta.lang) _updateLangAuthorHint(_loadedMeta.lang);
}
