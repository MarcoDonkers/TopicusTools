$(document).ready(function() {
    // 1. Inject a persistent floating UI widget
    if ($('#syncWidget').length === 0) {
        $('body').append(`
            <div id="syncWidget" style="position: fixed; bottom: 20px; right: 20px; z-index: 99999; background: #fff; padding: 15px; border-radius: 8px; box-shadow: 0 4px 15px rgba(0,0,0,0.2); border: 1px solid #ddd; width: 240px;">
                <h6 style="margin-top: 0; margin-bottom: 10px; font-weight: bold; font-size: 14px; text-align: center;">🛠️ Sync Builds</h6>
                <div style="display: flex; justify-content: space-between;">
                    <button id="btnCopyBuilds" style="padding: 5px 10px; cursor: pointer; border-radius: 4px; border: 1px solid #007bff; background: #007bff; color: white; width: 48%;">1. Copy</button>
                    <button id="btnPasteBuilds" style="padding: 5px 10px; cursor: pointer; border-radius: 4px; border: 1px solid #28a745; background: #28a745; color: white; width: 48%;">2. Paste</button>
                </div>
                <div style="margin-top: 10px; font-size: 12px; display: flex; align-items: center; justify-content: center;">
                    <input type="checkbox" id="chkSkipDeployed" checked style="margin-right: 6px; cursor: pointer;">
                    <label for="chkSkipDeployed" style="cursor: pointer; margin: 0; color: #333;">Skip already deployed</label>
                </div>
                <div id="syncStatus" style="font-size: 11px; margin-top: 10px; text-align: center; color: #555;">Ready</div>
            </div>
        `);
    }

    // 2. COPY LOGIC
    $('#btnCopyBuilds').on('click', function(e) {
        e.preventDefault();
        var state = {};
        
        $('table tbody tr').each(function() {
            var compId = $(this).find('.cbComponent').attr('componentid');
            var histText = $(this).find('div[id^="partialDiv_Version_"] .accordion-header button').first().text().replace(/\s+/g, ' ').trim();
            
            if (compId && histText) {
                state[compId] = histText;
            }
        });
        
        localStorage.setItem('qsp_build_state', JSON.stringify(state));
        $('#syncStatus').text(`✅ Copied ${Object.keys(state).length} builds!`).css('color', 'green');
    });

    // 3. PASTE LOGIC
    $('#btnPasteBuilds').on('click', function(e) {
        e.preventDefault();
        var savedData = localStorage.getItem('qsp_build_state');
        
        if (!savedData) {
            $('#syncStatus').text('❌ No copied data found!').css('color', 'red');
            return;
        }
        
        var state = JSON.parse(savedData);
        var skipDeployed = $('#chkSkipDeployed').is(':checked'); // Check toggle state
        var matchCount = 0;
        var skipCount = 0;
        var failCount = 0;
        
        $('table tbody tr').each(function() {
            var $row = $(this);
            var compId = $row.find('.cbComponent').attr('componentid');
            var $buildSelect = $row.find('select.build');
            
            if (compId && state[compId] && $buildSelect.length) {
                var cleanTarget = state[compId].toLowerCase().replace('.dar', '').replace('.zip', '').trim();
                
                // Read the current environment's history text for this row
                var currentHistory = $row.find('div[id^="partialDiv_Version_"] .accordion-header button').first().text().toLowerCase().replace(/\s+/g, ' ').trim();
                
                // Skip logic evaluating the checkbox state
                if (skipDeployed && currentHistory && (currentHistory.includes(cleanTarget) || cleanTarget.includes(currentHistory))) {
                    skipCount++;
                    return; // Skips to the next row
                }
                
                var $targetOption = $buildSelect.find('option').filter(function() {
                    var optText = $(this).text().toLowerCase().replace(/\s+/g, ' ').trim();
                    return optText.includes(cleanTarget) || cleanTarget.includes(optText);
                });
                
                if ($targetOption.length) {
                    // Apply build selection
                    $buildSelect.val($targetOption.first().val()).trigger('change');
                    
                    // Enable Deploy checkbox
                    $row.find('.cbComponent').prop('checked', true).trigger('change');
                    
                    // Enable Reset DB checkbox (if it exists)
                    $row.find('.cbDeleteDatabase').prop('checked', true).trigger('change');
                    
                    matchCount++;
                } else {
                    failCount++;
                    console.warn(`[Build Sync] Failed to match target "${cleanTarget}" on Component ID ${compId}`);
                }
            }
        });
        
        $('#syncStatus').html(`🚀 Applied: <b>${matchCount}</b> | Skipped: <b>${skipCount}</b> | Failed: <b style="color:${failCount > 0 ? 'red' : 'inherit'}">${failCount}</b>`).css('color', 'black');
    });
});