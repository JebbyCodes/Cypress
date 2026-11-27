// =====================================================================
// Persistent EA sign-in button.
// The startup auth modal can be dismissed and never reopened. This adds
// a fixed button that is visible whenever the user is logged out and
// calls the same showAuthModal() the startup flow uses, so the existing
// eaLogin -> MessageHandler.OnEaLogin path is reused unchanged.
// Append this block to the end of wwwroot/js/auth.js.
// =====================================================================
(function () {
    var BTN_ID = 'eaLoginPersistentBtn';

    function ensureButton() {
        var btn = document.getElementById(BTN_ID);
        if (btn) return btn;

        btn = document.createElement('button');
        btn.id = BTN_ID;
        btn.type = 'button';
        btn.textContent = 'Sign in with EA';
        btn.style.cssText = [
            'position:fixed',
            'right:24px',
            'bottom:24px',
            'z-index:9999',
            'padding:12px 22px',
            'border:none',
            'border-radius:12px',
            'background:linear-gradient(135deg,#E7BE40 0%,#E5A241 100%)',
            'color:#3a2a10',
            'font-weight:700',
            'font-size:14px',
            'letter-spacing:0.3px',
            'cursor:pointer',
            'box-shadow:0 6px 20px rgba(231,190,64,0.35)',
            'font-family:inherit'
        ].join(';');

        btn.addEventListener('click', function () {
            if (typeof showAuthModal === 'function') {
                showAuthModal();
            } else {
                // Fallback if auth.js is loaded in a different order.
                send('checkAuth', {});
            }
        });

        document.body.appendChild(btn);
        return btn;
    }

    function sync() {
        var btn = ensureButton();
        btn.style.display = window._eaLoggedIn ? 'none' : 'block';
    }

    // React whenever the existing identity updater runs (it is called from
    // handleAuthStatus, handleAuthLoginResult, handleAuthLogoutResult and
    // handleRegisterResult), so the button tracks real login state.
    if (typeof updateProfileIdentitySection === 'function' && !updateProfileIdentitySection.__eaWrapped) {
        var orig = updateProfileIdentitySection;
        window.updateProfileIdentitySection = function () {
            orig.apply(this, arguments);
            sync();
        };
        window.updateProfileIdentitySection.__eaWrapped = true;
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', sync);
    } else {
        sync();
    }
    // Safety net in case the auth status arrives before this block runs.
    setTimeout(sync, 1500);
})();
