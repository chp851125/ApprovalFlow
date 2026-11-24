// 單據表單：明細列新增 / 刪除 / 重新編號、即時合計、請假時數試算、加班時數
(function () {
    'use strict';
    var form = document.getElementById('documentForm');
    if (!form) return;

    var fmt = new Intl.NumberFormat('zh-TW', { maximumFractionDigits: 0 });
    function num(v) { var n = parseFloat(v); return isNaN(n) ? 0 : n; }

    // ---------------- 明細列 ----------------
    function renumber(list) {
        var prefix = list.getAttribute('data-prefix');
        var escaped = prefix.replace(/[.[\]]/g, '\\$&');
        var re = new RegExp('^' + escaped + '\\[[^\\]]*\\]');
        list.querySelectorAll('[data-line-row]').forEach(function (row, i) {
            row.querySelectorAll('[name]').forEach(function (el) {
                el.name = el.name.replace(re, prefix + '[' + i + ']');
            });
        });
    }

    function recalc(kind) {
        var list = form.querySelector('[data-line-list="' + kind + '"]');
        if (!list) return;
        var total = 0;
        list.querySelectorAll('[data-line-row]').forEach(function (row) {
            var amount = row.querySelector('[data-line-amount]');
            if (amount) { total += Math.round(num(amount.value)); return; }
            var qty = row.querySelector('[data-line-qty]');
            var price = row.querySelector('[data-line-price]');
            var sub = Math.round(num(qty && qty.value) * num(price && price.value));
            var subEl = row.querySelector('[data-line-subtotal]');
            if (subEl) subEl.textContent = fmt.format(sub);
            total += sub;
        });
        var out = form.querySelector('[data-line-total="' + kind + '"]');
        if (out) out.textContent = fmt.format(total);
    }

    form.addEventListener('click', function (e) {
        var add = e.target.closest('[data-line-add]');
        if (add) {
            var kind = add.getAttribute('data-line-add');
            var list = form.querySelector('[data-line-list="' + kind + '"]');
            var tpl = form.querySelector('[data-line-template="' + kind + '"]');
            if (list.querySelectorAll('[data-line-row]').length >= 30) { window.alert('明細最多 30 筆。'); return; }
            list.appendChild(tpl.content.cloneNode(true));
            renumber(list);
            recalc(kind);
            var rows = list.querySelectorAll('[data-line-row]');
            var first = rows[rows.length - 1].querySelector('input:not([type=hidden]), select');
            if (first) first.focus();
            return;
        }
        var remove = e.target.closest('[data-line-remove]');
        if (remove) {
            var row = remove.closest('[data-line-row]');
            var lst = row.closest('[data-line-list]');
            if (lst.querySelectorAll('[data-line-row]').length <= 1) {
                // 保留最後一列，只清空內容
                row.querySelectorAll('input').forEach(function (i) { i.value = ''; });
                row.querySelectorAll('select').forEach(function (s) { s.selectedIndex = 0; });
            } else {
                row.remove();
            }
            renumber(lst);
            recalc(lst.getAttribute('data-line-list'));
        }
    });

    form.addEventListener('input', function (e) {
        if (e.target.matches('[data-line-amount],[data-line-qty],[data-line-price]')) {
            recalc(e.target.closest('[data-line-list]').getAttribute('data-line-list'));
        }
    });

    form.querySelectorAll('[data-line-list]').forEach(function (l) {
        renumber(l);
        recalc(l.getAttribute('data-line-list'));
    });

    // ---------------- 請假時數試算 ----------------
    var leave = form.querySelector('[data-leave-form]');
    if (leave) {
        var start = leave.querySelector('[data-leave-start]');
        var end = leave.querySelector('[data-leave-end]');
        var type = leave.querySelector('[data-leave-type]');
        var result = leave.querySelector('[data-leave-result]');
        var timer = null;
        var updateLeave = function () {
            if (!start.value || !end.value) return;
            var params = new URLSearchParams({ start: start.value, end: end.value, leaveTypeId: type.value || '0' });
            var docId = form.getAttribute('data-document-id');
            if (docId) params.set('documentId', docId);
            fetch(form.getAttribute('data-leave-hours-url') + '?' + params.toString(), { headers: { 'Accept': 'application/json' } })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (d) {
                    if (!d) return;
                    var html = '<i class="bi bi-calculator me-1"></i>請假時數：<strong>' + d.text + '</strong>';
                    if (type.value !== '0') html += '　剩餘：' + d.remainingText;
                    if (d.minUnit && d.hours > 0 && d.hours % d.minUnit !== 0) html += '　<span class="text-danger">需為 ' + d.minUnit + ' 小時的倍數</span>';
                    if (d.insufficient) html += '　<span class="text-danger fw-semibold">餘額不足</span>';
                    result.innerHTML = html;
                    result.classList.toggle('is-warning', !!d.insufficient || d.hours <= 0);
                })
                .catch(function () { /* 試算失敗不影響送出，伺服器會再次計算 */ });
        };
        [start, end, type].forEach(function (el) {
            el.addEventListener('change', function () { clearTimeout(timer); timer = setTimeout(updateLeave, 200); });
        });
        updateLeave();
    }

    // ---------------- 加班時數 ----------------
    var ot = form.querySelector('[data-overtime-form]');
    if (ot) {
        var s = ot.querySelector('[data-ot-start]');
        var e2 = ot.querySelector('[data-ot-end]');
        var out = ot.querySelector('[data-ot-result]');
        var original = out.innerHTML;
        var updateOt = function () {
            if (!s.value || !e2.value) { out.innerHTML = original; return; }
            var a = s.value.split(':'), b = e2.value.split(':');
            var minutes = (b[0] * 60 + +b[1]) - (a[0] * 60 + +a[1]);
            if (minutes <= 0) {
                out.innerHTML = '<span class="text-danger">結束時間必須晚於開始時間（跨日請分兩張申請）</span>';
                return;
            }
            var hours = Math.floor(minutes / 30) / 2;
            out.innerHTML = '<i class="bi bi-calculator me-1"></i>加班時數：<strong>' + hours + ' 小時</strong>（以 0.5 小時為單位）';
        };
        s.addEventListener('change', updateOt);
        e2.addEventListener('change', updateOt);
        updateOt();
    }
})();
