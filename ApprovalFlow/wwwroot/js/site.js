// ApprovalFlow 共用前端行為（不使用 inline script，以利日後啟用嚴格 CSP）
(function () {
    'use strict';

    // 下拉選單變更即送出（例：快速切換使用者）
    document.addEventListener('change', function (e) {
        var el = e.target;
        if (el.classList && el.classList.contains('js-autosubmit') && el.value) {
            el.form && el.form.submit();
        }
    });

    // data-confirm：送出前確認
    document.addEventListener('submit', function (e) {
        var form = e.target;
        var submitter = e.submitter;
        var msg = (submitter && submitter.getAttribute('data-confirm')) || form.getAttribute('data-confirm');
        if (msg && !window.confirm(msg)) {
            e.preventDefault();
            return;
        }
        // 防止重複送出
        if (!form.hasAttribute('data-allow-resubmit')) {
            if (form.dataset.submitting === '1') { e.preventDefault(); return; }
            form.dataset.submitting = '1';
            setTimeout(function () { form.dataset.submitting = ''; }, 4000);
        }
    });

    // 成功訊息自動淡出
    window.addEventListener('load', function () {
        document.querySelectorAll('.js-autodismiss').forEach(function (el) {
            setTimeout(function () {
                if (window.bootstrap) bootstrap.Alert.getOrCreateInstance(el).close();
            }, 5000);
        });
    });

    // 複製文字：<button class="js-copy" data-copy-target="#id">
    document.addEventListener('click', function (e) {
        var btn = e.target.closest && e.target.closest('.js-copy');
        if (!btn) return;
        var target = document.querySelector(btn.getAttribute('data-copy-target'));
        if (!target) return;
        var text = target.textContent.trim();
        var done = function () {
            var original = btn.innerHTML;
            btn.innerHTML = '<i class="bi bi-check2 me-1"></i>已複製';
            setTimeout(function () { btn.innerHTML = original; }, 2000);
        };
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(text).then(done);
        } else {
            var range = document.createRange();
            range.selectNodeContents(target);
            var sel = window.getSelection();
            sel.removeAllRanges();
            sel.addRange(range);
            try { document.execCommand('copy'); done(); } catch (err) { /* 已選取，使用者可自行複製 */ }
        }
    });

    // 取得 CSRF token（供 fetch 使用）
    window.af = window.af || {};
    window.af.csrfToken = function () {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    };
})();
