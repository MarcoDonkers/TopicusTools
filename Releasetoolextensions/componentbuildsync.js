// Sync Builds v2 for the QSP Release Tool (Components page).
// Copy  : reads the HISTORY column (build deployed now) of every component into localStorage AND the clipboard
//         (JSON, indented) so it can be pasted on another release tool (T -> O).
// Paste : takes the list from the clipboard (or the text box when the browser refuses clipboard reads),
//         selects that build in SELECT BUILD, ticks Deploy, Reset DB (if the row has it) and Reset config.
//         "Skip already deployed" leaves rows alone whose history already shows that build.
// Text  : the text box also accepts the output of "Copy overview to clipboard" ("Component - Build" lines).
$(document).ready(function () {
    if ($('#syncWidget').length) return;
    $('body').append(`
        <div id="syncWidget" style="position: fixed; bottom: 20px; right: 20px; z-index: 99999; background: #fff; padding: 12px; border-radius: 8px; box-shadow: 0 4px 15px rgba(0,0,0,0.2); border: 1px solid #ddd; width: 300px; font-size: 12px;">
            <h6 style="margin: 0 0 8px; font-weight: bold; font-size: 14px; text-align: center;">🛠️ Sync Builds</h6>
            <div style="display: flex; gap: 4%;">
                <button id="btnCopyBuilds" style="padding: 5px 8px; cursor: pointer; border-radius: 4px; border: 1px solid #007bff; background: #007bff; color: white; width: 48%;">1. Copy</button>
                <button id="btnPasteBuilds" style="padding: 5px 8px; cursor: pointer; border-radius: 4px; border: 1px solid #28a745; background: #28a745; color: white; width: 48%;">2. Paste</button>
            </div>
            <textarea id="syncText" rows="10" placeholder="Copy puts the list here and on the clipboard. Paste reads the clipboard, or this box if it is not empty." style="width: 100%; margin-top: 8px; font-size: 11px; font-family: monospace; box-sizing: border-box;"></textarea>
            <div style="margin-top: 6px; display: flex; align-items: center; gap: 6px;">
                <input type="checkbox" id="chkSkipDeployed" checked style="cursor: pointer;">
                <label for="chkSkipDeployed" style="cursor: pointer; margin: 0; color: #333;">Skip already deployed</label>
            </div>
            <div style="margin-top: 4px; display: flex; align-items: center; gap: 6px;">
                <input type="checkbox" id="chkResetDb" checked style="cursor: pointer;">
                <label for="chkResetDb" style="cursor: pointer; margin: 0; color: #333;">Tick Reset DB where available</label>
            </div>
            <div id="syncStatus" style="margin-top: 8px; text-align: center; color: #555;">Ready</div>
        </div>`);

    var norm = function (s) { return (s || '').toLowerCase().replace(/\.dar$/, '').replace(/\.zip$/, '').replace(/\s+/g, ' ').trim(); };
    var status = function (html, color) { $('#syncStatus').html(html).css('color', color || '#555'); };

    function readTable() {
        var state = {};
        $('table tbody tr').each(function () {
            var name = $(this).find('label[for="lblComponent"]').text().trim();
            var hist = $(this).find('div[id^="partialDiv_Version_"] .accordion-header button').first().text().replace(/\s+/g, ' ').trim();
            if (name && hist) state[name] = hist;
        });
        return state;
    }

    // Accepts JSON {name: build} or "Component - Build" lines (the tool's own overview format).
    function parseText(text) {
        text = (text || '').trim();
        if (!text) return null;
        if (text[0] === '{') { try { return JSON.parse(text); } catch (e) { return null; } }
        var state = {};
        text.split(/\r?\n/).forEach(function (line) {
            var m = line.match(/^(.+?)\s+-\s+(.*)$/);
            if (m && m[2].trim()) state[m[1].trim()] = m[2].trim();
        });
        return Object.keys(state).length ? state : null;
    }

    function copyToClipboard(text) {
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return fallbackCopy(text); });
        }
        return Promise.resolve(fallbackCopy(text));
    }
    function fallbackCopy(text) {
        var ta = document.createElement('textarea');
        ta.value = text; ta.style.position = 'fixed'; ta.style.opacity = '0';
        document.body.appendChild(ta); ta.focus(); ta.select();
        var ok = false; try { ok = document.execCommand('copy'); } catch (e) { }
        document.body.removeChild(ta);
        return ok;
    }

    $('#btnCopyBuilds').on('click', function (e) {
        e.preventDefault();
        var state = readTable();
        var json = JSON.stringify(state, null, 2);
        localStorage.setItem('qsp_build_state', json);
        $('#syncText').val(json);
        copyToClipboard(json).then(function (ok) {
            status('✅ Copied ' + Object.keys(state).length + ' builds' + (ok ? ' (clipboard + box)' : ' (box only: clipboard blocked, copy the box text)'), ok ? 'green' : '#b26a00');
        });
    });

    function applyState(state) {
        var skipDeployed = $('#chkSkipDeployed').is(':checked');
        var tickResetDb = $('#chkResetDb').is(':checked');
        var matched = 0, skipped = 0, failed = [], unknown = [];
        $('table tbody tr').each(function () {
            var $row = $(this);
            var name = $row.find('label[for="lblComponent"]').text().trim();
            var $sel = $row.find('select.build');
            if (!name || !$sel.length) return;
            if (!state[name]) { unknown.push(name); return; }
            var target = norm(state[name]);
            var current = norm($row.find('div[id^="partialDiv_Version_"] .accordion-header button').first().text());
            if (skipDeployed && current && (current === target || current.indexOf(target) >= 0 || target.indexOf(current) >= 0)) { skipped++; return; }
            var $opt = $sel.find('option').filter(function () { return norm($(this).text()) === target; });
            if (!$opt.length) $opt = $sel.find('option').filter(function () { var t = norm($(this).text()); return t && (t.indexOf(target) >= 0 || target.indexOf(t) >= 0); });
            if ($opt.length) {
                $sel.val($opt.first().val()).trigger('change');
                $row.find('.cbComponent').prop('checked', true).trigger('change');
                if (tickResetDb) $row.find('.cbDeleteDatabase').prop('checked', true).trigger('change');
                $row.find('.cbDeleteConfiguration').prop('checked', true).trigger('change');
                matched++;
            } else {
                failed.push(name + ' → ' + state[name]);
                console.warn('[Build Sync] no option for "' + state[name] + '" on ' + name);
            }
        });
        status('🚀 Applied <b>' + matched + '</b> | skipped <b>' + skipped + '</b> | not found <b style="color:' + (failed.length ? 'red' : 'inherit') + '">' + failed.length + '</b>' +
            (failed.length ? '<div style="text-align:left;margin-top:4px;color:#b3261e;max-height:90px;overflow:auto">' + failed.map(function (f) { return '• ' + f; }).join('<br>') + '</div>' : '') +
            (unknown.length ? '<div style="text-align:left;margin-top:4px;color:#777">not in source: ' + unknown.join(', ') + '</div>' : ''), 'black');
        if (failed.length) console.warn('[Build Sync] not found:\n' + failed.join('\n'));
    }

    $('#btnPasteBuilds').on('click', function (e) {
        e.preventDefault();
        var boxed = parseText($('#syncText').val());
        if (boxed) { applyState(boxed); return; }
        var fromClipboard = function (text) {
            var st = parseText(text);
            if (st) { $('#syncText').val(text.trim()); applyState(st); return true; }
            return false;
        };
        if (navigator.clipboard && navigator.clipboard.readText && window.isSecureContext) {
            navigator.clipboard.readText().then(function (text) {
                if (!fromClipboard(text)) {
                    var saved = localStorage.getItem('qsp_build_state');
                    if (saved) applyState(JSON.parse(saved)); else status('❌ Nothing usable on the clipboard; paste the list in the box first', 'red');
                }
            }, function () {
                var saved = localStorage.getItem('qsp_build_state');
                if (saved) applyState(JSON.parse(saved)); else status('❌ Clipboard read blocked; paste the list in the box (Ctrl+V) and click Paste again', 'red');
            });
        } else {
            var saved = localStorage.getItem('qsp_build_state');
            if (saved) applyState(JSON.parse(saved)); else status('❌ Clipboard not available here; paste the list in the box (Ctrl+V) and click Paste again', 'red');
        }
    });
});
