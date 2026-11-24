// 流程範本編輯：新增 / 刪除 / 排序關卡，依簽核人來源切換欄位
(function () {
    'use strict';
    var list = document.getElementById('stepList');
    var tpl = document.getElementById('stepTemplate');
    var form = document.getElementById('templateForm');
    if (!list || !tpl || !form) return;

    function rows() { return Array.prototype.slice.call(list.querySelectorAll('[data-step-row]')); }

    // 重新編號：name="Steps[n].X"、id/for 也一併更新
    function renumber() {
        rows().forEach(function (row, index) {
            row.querySelector('[data-step-no]').textContent = index + 1;
            row.querySelectorAll('[name]').forEach(function (el) {
                el.name = el.name.replace(/^Steps\[[^\]]*\]/, 'Steps[' + index + ']');
            });
            row.querySelectorAll('[id]').forEach(function (el) {
                var old = el.id;
                el.id = old.replace(/-[^-]+$/, '-' + index);
                var label = row.querySelector('label[for="' + old + '"]');
                if (label) label.htmlFor = el.id;
            });
        });
        document.getElementById('stepEmpty').hidden = rows().length > 0;
    }

    function applySource(row) {
        var source = row.querySelector('[data-step-source]').value;
        row.querySelectorAll('[data-for-source]').forEach(function (el) {
            var show = el.getAttribute('data-for-source').split(' ').indexOf(source) >= 0;
            el.classList.toggle('d-none', !show);
        });
    }

    list.addEventListener('change', function (e) {
        var row = e.target.closest('[data-step-row]');
        if (!row) return;
        if (e.target.matches('[data-step-source]')) applySource(row);
        if (e.target.matches('[data-step-condition]')) {
            row.querySelector('[data-condition-fields]').classList.toggle('d-none', !e.target.checked);
        }
    });

    list.addEventListener('click', function (e) {
        var btn = e.target.closest('button');
        if (!btn) return;
        var row = btn.closest('[data-step-row]');
        if (btn.hasAttribute('data-step-remove')) {
            if (rows().length <= 1) { window.alert('流程至少需要一個關卡。'); return; }
            row.remove();
        } else if (btn.hasAttribute('data-step-up') && row.previousElementSibling) {
            list.insertBefore(row, row.previousElementSibling);
        } else if (btn.hasAttribute('data-step-down') && row.nextElementSibling) {
            list.insertBefore(row.nextElementSibling, row);
        } else {
            return;
        }
        renumber();
    });

    document.querySelector('[data-step-add]').addEventListener('click', function () {
        var fragment = tpl.content.cloneNode(true);
        list.appendChild(fragment);
        renumber();
        var added = rows()[rows().length - 1];
        applySource(added);
        added.querySelector('input[name$=".Name"]').focus();
    });

    form.addEventListener('submit', renumber);
    renumber();
})();
