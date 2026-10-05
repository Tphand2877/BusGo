(() => {
    'use strict';
    const root = document.documentElement;
    const system = window.matchMedia('(prefers-color-scheme: dark)');
    const valid = value => ['light', 'dark', 'system'].includes(value);
    let preference = valid(root.dataset.appearance) ? root.dataset.appearance : 'system';

    function savedPreference() {
        const value = document.cookie.split(';').map(part => part.trim())
            .find(part => part.startsWith('BusGo.Appearance='))?.split('=')[1];
        return valid(value) ? value : 'system';
    }

    function apply() {
        const dark = preference === 'dark' || (preference === 'system' && system.matches);
        root.dataset.appearance = preference;
        root.dataset.theme = dark ? 'dark' : 'light';
        document.querySelector('meta[name="theme-color"]')?.setAttribute('content', dark ? '#101714' : '#f5f7f8');
        document.querySelectorAll('[data-theme-toggle]').forEach(toggle => {
            const label = dark ? toggle.dataset.labelLight : toggle.dataset.labelDark;
            toggle.setAttribute('aria-label', label);
            toggle.setAttribute('title', label);
        });
        document.querySelectorAll('[data-appearance-form] input[name="appearance"]').forEach(input => {
            input.checked = input.value === preference;
        });
    }

    function transitionTheme(updateFn, event) {
        const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        if (reduceMotion || typeof document.startViewTransition !== 'function') {
            if (!reduceMotion) {
                root.classList.add('is-theme-transitioning');
                updateFn();
                setTimeout(() => root.classList.remove('is-theme-transitioning'), 450);
            } else {
                updateFn();
            }
            return;
        }

        // Determine transition origin: click coordinates or toggle button center
        let x = window.innerWidth - 60;
        let y = 40;
        if (event && typeof event.clientX === 'number' && typeof event.clientY === 'number' && (event.clientX !== 0 || event.clientY !== 0)) {
            x = event.clientX;
            y = event.clientY;
        } else {
            const toggle = document.querySelector('[data-theme-toggle]');
            if (toggle) {
                const rect = toggle.getBoundingClientRect();
                x = rect.left + rect.width / 2;
                y = rect.top + rect.height / 2;
            }
        }

        const endRadius = Math.hypot(
            Math.max(x, window.innerWidth - x),
            Math.max(y, window.innerHeight - y)
        );

        try {
            const transition = document.startViewTransition(() => {
                updateFn();
            });

            transition.ready.then(() => {
                document.documentElement.animate(
                    {
                        clipPath: [
                            `circle(0px at ${x}px ${y}px)`,
                            `circle(${endRadius}px at ${x}px ${y}px)`
                        ]
                    },
                    {
                        duration: 460,
                        easing: 'cubic-bezier(0.2, 0, 0.25, 1)',
                        pseudoElement: '::view-transition-new(root)'
                    }
                );
            }).catch(() => {
                // If animation fails or is aborted, the new state is already applied.
            });
        } catch (e) {
            updateFn();
        }
    }

    function save(value, event) {
        if (!valid(value)) return;
        preference = value;
        document.cookie = `BusGo.Appearance=${value}; Path=/; Max-Age=31536000; SameSite=Lax${location.protocol === 'https:' ? '; Secure' : ''}`;
        transitionTheme(apply, event);
        const form = document.querySelector('[data-appearance-form]');
        if (form) {
            const status = form.querySelector('[role="status"]');
            if (status) {
                status.textContent = savedPreference() === value
                    ? form.dataset.saved : form.dataset.saveError;
            }
        }
    }

    // Synchronous head script: resolve the OS preference before the first paint without animation.
    apply();
    system.addEventListener('change', () => {
        if (preference === 'system') {
            transitionTheme(apply);
        }
    });

    document.addEventListener('DOMContentLoaded', () => {
        apply();
        document.querySelectorAll('[data-theme-toggle]').forEach(toggle => {
            toggle.addEventListener('click', event => {
                // Preserve opening the settings link in another tab.
                if (event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
                event.preventDefault();
                save(root.dataset.theme === 'dark' ? 'light' : 'dark', event);
            });
        });
        document.querySelector('[data-appearance-form]')?.addEventListener('submit', event => {
            event.preventDefault();
            const chosen = new FormData(event.currentTarget).get('appearance');
            save(chosen, event);
        });
    });

    function restore() {
        const currentSaved = savedPreference();
        if (currentSaved !== preference) {
            preference = currentSaved;
            transitionTheme(apply);
        }
    }
    window.addEventListener('pageshow', restore);
    window.addEventListener('focus', restore);
})();
