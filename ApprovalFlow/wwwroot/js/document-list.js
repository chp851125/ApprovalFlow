// 待辦清單：批次核准勾選（桌機表格與手機卡片各有一組勾選框，以單號同步）
(function () {
    'use strict';
    var form = document.getElementById('batchForm');
    if (!form || !form.querySelector('[data-batch-all]')) return;

    var all = form.querySelector('[data-batch-all]');
    var counter = form.querySelector('[data-batch-count]');
    var submit = form.querySelector('[data-batch-submit]');

    function items() { return Array.prototype.slice.call(form.querySelectorAll('[data-batch-item]')); }

    function refresh() {
        var selected = {};
        items().forEach(function (cb) {
            // 勾選時才送出對應的版本號，未勾選的 hidden 欄位停用
            var version = cb.parentElement.querySelector('[data-batch-version]');
            if (version) version.disabled = !cb.checked;
            if (cb.checked) selected[cb.value] = true;
        });
        var n = Object.keys(selected).length;
        counter.textContent = n;
        submit.disabled = n === 0;
    }

    form.addEventListener('change', function (e) {
        if (e.target === all) {
            items().forEach(function (cb) { cb.checked = all.checked; });
        } else if (e.target.matches('[data-batch-item]')) {
            // 同一張單在表格與卡片各有一個勾選框，保持一致
            items().forEach(function (cb) { if (cb.value === e.target.value) cb.checked = e.target.checked; });
        }
        refresh();
    });

    // 送出前只保留可見版面的勾選框，避免同一張單重複送出
    form.addEventListener('submit', function () {
        items().forEach(function (cb) {
            if (cb.offsetParent === null) {
                cb.disabled = true;
                var v = cb.parentElement.querySelector('[data-batch-version]');
                if (v) v.disabled = true;
            }
        });
    });

    refresh();
})();
