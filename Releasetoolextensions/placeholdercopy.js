$(document).ready(function() {
    const sqlServerMap = {
        'o1': 'mgqsp-ov-sql01.finance.lab',
        'o2': 'mgqsp-ov-sql03.finance.lab',
        'o3': 'mgqsp-ov-sql04.finance.lab',
        'o6': 'mgqsp-ov-sql01.finance.lab',
        'o8': 'mgqsp-ov-sql03.finance.lab',
        'o9': 'mgqsp-ov-sql04.finance.lab',
        'o10': 'mgqsp-ov-sql02.finance.lab',
        'o11': 'mgqsp-ov-sql03.finance.lab',
        'o12': 'mgqsp-ov-sql02.finance.lab',
        'o13': 'mgqsp-ov-sql02.finance.lab',
        'o14': 'mgqsp-ov-sql03.finance.lab',
        'o15': 'mgqsp-ov-sql04.finance.lab',
        'o16': 'mgqsp-ov-sql01.finance.lab',
        'o18': 'mgqsp-ov-sql03.finance.lab',
        'o20': 'mgqsp-ov-sql02.finance.lab',
        'o21': 'mgqsp-ov-sql04.finance.lab',
        'o22': 'mgqsp-ov-sql01.finance.lab',
        'o23': 'mgqsp-ov-sql03.finance.lab',
        'o24': 'mgqsp-ov-sql04.finance.lab'
    };

    if ($('#placeholderSyncWidget').length === 0) {
        var envOptions = $('#environment').html();

        $('body').append(`
            <div id="placeholderSyncWidget" style="position: fixed; bottom: 20px; right: 20px; z-index: 99999; background: #fff; padding: 15px; border-radius: 8px; box-shadow: 0 4px 15px rgba(0,0,0,0.2); border: 1px solid #ddd; width: 320px;">
                <h6 style="margin-top: 0; margin-bottom: 15px; font-weight: bold; font-size: 14px; text-align: center;">🔄 Sync Placeholders</h6>
                
                <div style="margin-bottom: 10px;">
                    <label style="font-size: 11px; display: block; margin-bottom: 3px;">Source Environment:</label>
                    <select id="syncSource" style="width: 100%; padding: 4px; font-size: 12px; border: 1px solid #ccc; border-radius: 4px;">
                        ${envOptions}
                    </select>
                </div>

                <div style="margin-bottom: 15px;">
                    <label style="font-size: 11px; display: block; margin-bottom: 3px;">Target Environment:</label>
                    <select id="syncTarget" style="width: 100%; padding: 4px; font-size: 12px; border: 1px solid #ccc; border-radius: 4px;">
                        ${envOptions}
                    </select>
                </div>

                <button id="btnStartSync" style="width: 100%; padding: 6px 10px; cursor: pointer; border-radius: 4px; border: 1px solid #007bff; background: #007bff; color: white; font-weight: bold;">▶ Start Sync</button>
                
                <div id="syncProgress" style="font-size: 11px; margin-top: 10px; text-align: center; color: #555; word-wrap: break-word;">Ready</div>
                
                <textarea id="syncChangelog" style="width: 100%; height: 120px; font-size: 10px; margin-top: 10px; display: none; white-space: pre; overflow-wrap: normal; overflow-x: scroll; border: 1px solid #ccc; border-radius: 4px; padding: 4px;" readonly></textarea>
                <button id="btnCopyChangelog" style="display: none; width: 100%; padding: 4px; font-size: 11px; margin-top: 5px; cursor: pointer; border-radius: 4px; border: 1px solid #6c757d; background: #f8f9fa; color: #333;">📋 Copy Changelog</button>
            </div>
        `);

        $('#syncSource').val('16'); // default o12
        $('#syncTarget').val('29'); // default o23
    }

    // Handle copying the changelog to clipboard
    $('#btnCopyChangelog').on('click', function(e) {
        e.preventDefault();
        navigator.clipboard.writeText($('#syncChangelog').val()).then(() => {
            var $btn = $(this);
            var originalText = $btn.text();
            $btn.text('✅ Copied!');
            setTimeout(() => { $btn.text(originalText); }, 2000);
        });
    });

    $('#btnStartSync').on('click', async function(e) {
        e.preventDefault();
        
        const sourceId = $('#syncSource').val();
        const targetId = $('#syncTarget').val();
        const srcName = $('#syncSource option:selected').text().trim();
        const tgtName = $('#syncTarget option:selected').text().trim();
        
        if (sourceId === targetId) {
            $('#syncProgress').text('❌ Source and Target must be different.').css('color', 'red');
            return;
        }

        $('#btnStartSync').prop('disabled', true).css('opacity', '0.5');
        $('#syncChangelog').hide().val('');
        $('#btnCopyChangelog').hide();
        
        let changelogText = "";

        const srcNumMatch = srcName.match(/\d+/);
        const tgtNumMatch = tgtName.match(/\d+/);
        const srcNum = srcNumMatch ? srcNumMatch[0] : null;
        const tgtNum = tgtNumMatch ? tgtNumMatch[0] : null;

        const srcSql = sqlServerMap[srcName.toLowerCase()];
        const tgtSql = sqlServerMap[tgtName.toLowerCase()];

        const applyReplacements = (str) => {
            if (!str) return str;
            let newStr = str;

            // 1. Environment Names (o12 -> o23, O12 -> O23)
            const regexEnv = new RegExp(srcName, 'gi');
            newStr = newStr.replace(regexEnv, function(match) {
                const isUpper = match.charAt(0) === match.charAt(0).toUpperCase();
                const replacementStart = isUpper ? tgtName.charAt(0).toUpperCase() : tgtName.charAt(0).toLowerCase();
                return replacementStart + tgtNum;
            });

            // 2. FRC Strings (FRC012 -> FRC023)
            if (srcNum && tgtNum) {
                const regexFrc = new RegExp('FRC(0*)' + srcNum, 'gi');
                newStr = newStr.replace(regexFrc, function(match, zeros) {
                    const prefix = match.substring(0, 3);
                    return prefix + zeros + tgtNum;
                });
            }

            // 3. SQL Server Names (Preserve Hostname Casing)
            if (srcSql && tgtSql) {
                const regexSql = new RegExp(srcSql.replace(/\./g, '\\.'), 'gi');
                newStr = newStr.replace(regexSql, function(match) {
                    const isUpper = match.charAt(0) === match.charAt(0).toUpperCase();
                    if (isUpper) {
                        // Uppercase the hostname, keep the domain lowercase
                        let parts = tgtSql.split('.');
                        parts[0] = parts[0].toUpperCase();
                        return parts.join('.');
                    } else {
                        return tgtSql.toLowerCase();
                    }
                });
            }

            return newStr;
        };

        const components = [];
        $('#component option').each(function() {
            components.push({ id: $(this).val(), name: $(this).text() });
        });

        const pageToken = $('input[name="__RequestVerificationToken"]').val();
        let successCount = 0;
        let failCount = 0;

        for (const comp of components) {
            $('#syncProgress').text(`🔄 Fetching ${comp.name}...`).css('color', '#007bff');

            try {
                let responseHtml = await $.ajax({
                    url: '/Placeholders/PlaceholdersPartialView',
                    type: "GET",
                    data: { component: comp.id, environment: sourceId }
                });

                let $parsed = $(responseHtml);
                let dbData = $parsed.find('#tbDatabase').val() || '';
                let configDbData = $parsed.find('#tbComponentConfigurationDatabase').val() || '';

                let newDbData = applyReplacements(dbData);
                let newConfigDbData = applyReplacements(configDbData);
                
                // Log DB changes
                if (dbData !== newDbData) {
                    changelogText += `[${comp.name}] Component DB: "${dbData}" -> "${newDbData}"\n`;
                }
                if (configDbData !== newConfigDbData) {
                    changelogText += `[${comp.name}] Config DB: "${configDbData}" -> "${newConfigDbData}"\n`;
                }

                let postData = {
                    __RequestVerificationToken: $parsed.find('input[name="__RequestVerificationToken"]').val() || pageToken,
                    Omgeving: targetId,
                    Component: comp.id,
                    tbDatabase: newDbData,
                    tbComponentConfigurationDatabase: newConfigDbData
                };

                let keyCount = 0;
                $parsed.find('tbody tr').each(function(index) {
                    let key = $(this).find('input[id^="tbKey-"]').val() || '';
                    let val = $(this).find('input[id^="tbValue-"]').val() || '';

                    if (key) {
                        let newVal = applyReplacements(val);
                        
                        // Log Placeholder changes
                        if (val !== newVal) {
                            changelogText += `[${comp.name}] ${key}: "${val}" -> "${newVal}"\n`;
                        }
                        
                        postData[`tbKey-${index}`] = key;
                        postData[`tbValue-${index}`] = newVal;
                        keyCount++;
                    }
                });

                $('#syncProgress').text(`📤 Saving ${comp.name}...`);

                await $.ajax({
                    url: '/Placeholders/SavePlaceholders',
                    type: 'POST',
                    data: postData
                });

                successCount++;
            } catch (err) {
                console.error(`Failed to sync ${comp.name}`, err);
                failCount++;
            }
            
            await new Promise(r => setTimeout(r, 250)); 
        }

        $('#syncProgress').html(`✅ Done! Saved: <b>${successCount}</b> | Failed: <b>${failCount}</b>`).css('color', failCount > 0 ? 'orange' : 'green');
        $('#btnStartSync').prop('disabled', false).css('opacity', '1');
        
        // Show changelog if replacements occurred
        if (changelogText !== "") {
            $('#syncChangelog').val(changelogText).show();
            $('#btnCopyChangelog').show();
        } else {
            $('#syncChangelog').val("Sync complete. No string replacements were made.").show();
        }
        
        if ($('#environment').val() === targetId) {
            $('#environment').trigger('change');
        }
    });
});