const menuToggle = document.querySelector('.menu-toggle');
const mainNav = document.querySelector('#main-nav');

function setMenuExpanded(expanded) {
    menuToggle?.setAttribute('aria-expanded', String(expanded));
    mainNav?.classList.toggle('open', expanded);
}

menuToggle?.addEventListener('click', function () {
    setMenuExpanded(this.getAttribute('aria-expanded') !== 'true');
});

mainNav?.addEventListener('click', function (event) {
    if (event.target.closest('a')) setMenuExpanded(false);
});

document.addEventListener('keydown', function (event) {
    if (event.key === 'Escape' && menuToggle?.getAttribute('aria-expanded') === 'true') {
        setMenuExpanded(false);
        menuToggle.focus();
    }
});

document.addEventListener('click', function (event) {
    if (menuToggle?.getAttribute('aria-expanded') === 'true'
        && !menuToggle.contains(event.target)
        && !mainNav?.contains(event.target)) {
        setMenuExpanded(false);
    }

    const print = event.target.closest('[data-print]');
    if (print) { event.preventDefault(); window.print(); }
    const swap = event.target.closest('[data-swap]');
    if (swap) {
        event.preventDefault();
        const form = swap.closest('form');
        const origin = form?.querySelector('[name="Origin"]');
        const destination = form?.querySelector('[name="Destination"]');
        if (origin && destination) {
            const previousOrigin = origin.value;
            origin.value = destination.value;
            destination.value = previousOrigin;
            origin.dispatchEvent(new Event('input', { bubbles: true }));
            destination.dispatchEvent(new Event('input', { bubbles: true }));
            origin.dispatchEvent(new Event('change', { bubbles: true }));
            destination.dispatchEvent(new Event('change', { bubbles: true }));
        }
    }
});

function localDateWithOffset(offset) {
    const date = new Date();
    date.setHours(12, 0, 0, 0);
    date.setDate(date.getDate() + offset);
    return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}`;
}

document.querySelectorAll('[data-trip-search-form]').forEach(function (form) {
    const dateInput = form.querySelector('[name="Date"]');
    const dateShortcuts = [...form.querySelectorAll('[data-trip-date-offset]')];

    function updateDateShortcuts() {
        dateShortcuts.forEach(function (button) {
            const selected = dateInput.value === localDateWithOffset(Number(button.dataset.tripDateOffset));
            button.setAttribute('aria-pressed', String(selected));
        });
    }

    dateShortcuts.forEach(function (button) {
        button.addEventListener('click', function () {
            dateInput.value = localDateWithOffset(Number(this.dataset.tripDateOffset));
            dateInput.dispatchEvent(new Event('change', { bubbles: true }));
        });
    });
    dateInput.addEventListener('input', updateDateShortcuts);
    dateInput.addEventListener('change', updateDateShortcuts);
    updateDateShortcuts();
});

function normalizeLocation(value) {
    return value.trim().toLocaleLowerCase('vi').normalize('NFD')
        .replace(/[\u0300-\u036f]/g, '').replace(/đ/g, 'd').replace(/[\s.\-–]/g, '');
}

const locationPickers = [...document.querySelectorAll('[data-location-picker]')].map(function (picker) {
    const input = picker.querySelector('[role="combobox"]');
    const list = picker.querySelector('[role="listbox"]');
    const options = [...list.querySelectorAll('[data-location-option]')];
    const searchValues = options.map(option => normalizeLocation(option.dataset.search));
    const emptyMessage = list.querySelector('.trip-search-no-suggestions');
    const visible = [];
    let activeIndex = -1;

    function setActive(index) {
        if (activeIndex >= 0) visible[activeIndex]?.setAttribute('aria-selected', 'false');
        activeIndex = index;
        const option = visible[index];
        if (option) {
            option.setAttribute('aria-selected', 'true');
            input.setAttribute('aria-activedescendant', option.id);
            option.scrollIntoView({ block: 'nearest' });
        } else {
            input.removeAttribute('aria-activedescendant');
        }
    }

    function closeSuggestions() {
        setActive(-1);
        list.hidden = true;
        input.setAttribute('aria-expanded', 'false');
    }

    function showSuggestions() {
        setActive(-1);
        visible.length = 0;
        const query = normalizeLocation(input.value);
        options.forEach(function (option, index) {
            option.hidden = query !== '' && !searchValues[index].includes(query);
            if (!option.hidden) visible.push(option);
        });
        emptyMessage.hidden = visible.length > 0;
        list.hidden = false;
        input.setAttribute('aria-expanded', 'true');
    }

    function selectOption(option) {
        input.value = option.dataset.value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
        closeSuggestions();
    }

    input.addEventListener('focus', showSuggestions);
    input.addEventListener('input', function () {
        if (document.activeElement === input) showSuggestions();
        else closeSuggestions();
    });
    input.addEventListener('change', function () {
        if (document.activeElement !== input) closeSuggestions();
    });
    input.addEventListener('blur', closeSuggestions);
    input.addEventListener('keydown', function (event) {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            if (list.hidden) showSuggestions();
            if (!visible.length) return;
            const index = activeIndex < 0
                ? (event.key === 'ArrowDown' ? 0 : visible.length - 1)
                : (activeIndex + (event.key === 'ArrowDown' ? 1 : -1) + visible.length) % visible.length;
            setActive(index);
        } else if (event.key === 'Enter' && !list.hidden && activeIndex >= 0) {
            event.preventDefault();
            selectOption(visible[activeIndex]);
        } else if (event.key === 'Escape' && !list.hidden) {
            event.preventDefault();
            closeSuggestions();
        } else if (event.key === 'Tab') {
            closeSuggestions();
        }
    });
    list.addEventListener('pointerdown', function (event) {
        if (event.target.closest('[data-location-option]')) event.preventDefault();
    });
    list.addEventListener('click', function (event) {
        const option = event.target.closest('[data-location-option]');
        if (option && !option.hidden) selectOption(option);
    });
    return { element: picker, close: closeSuggestions };
});

if (locationPickers.length) {
    document.addEventListener('pointerdown', function (event) {
        locationPickers.forEach(function (picker) {
            if (!picker.element.contains(event.target)) picker.close();
        });
    });
}

const searchFilters = document.querySelector('[data-search-filters]');
if (searchFilters) {
    const desktopFilters = window.matchMedia('(min-width: 851px)');
    function updateFilterDisclosure() {
        searchFilters.open = desktopFilters.matches;
    }
    desktopFilters.addEventListener('change', updateFilterDisclosure);
    updateFilterDisclosure();
}

document.addEventListener('submit', function (event) {
    const message = event.target.dataset.confirm;
    if (message && !window.confirm(message)) event.preventDefault();
});

// Keep browser constraint messages in the selected application language.
document.addEventListener('invalid', function (event) {
    const control = event.target;
    if (typeof control.setCustomValidity !== 'function') return;
    control.setCustomValidity('');
    const validity = control.validity;
    const text = document.body.dataset;
    let message = '';
    if (validity.valueMissing) message = text.validationRequired;
    else if (validity.typeMismatch) message = control.type === 'email' ? text.validationEmail : text.validationFormat;
    else if (validity.badInput) message = control.type === 'number' ? text.validationNumber : text.validationFormat;
    else if (validity.rangeUnderflow) message = text.validationMin.replace('{0}', control.min);
    else if (validity.rangeOverflow) message = text.validationMax.replace('{0}', control.max);
    else if (validity.tooShort) message = text.validationShort.replace('{0}', control.minLength);
    else if (validity.tooLong) message = text.validationLong.replace('{0}', control.maxLength);
    else if (validity.patternMismatch || validity.stepMismatch) message = text.validationFormat;
    control.setCustomValidity(message);
}, true);

function clearConstraintMessage(event) {
    if (typeof event.target.setCustomValidity === 'function') event.target.setCustomValidity('');
}
document.addEventListener('input', clearConstraintMessage);

// Hero landscape photo switcher (Cinematic dual-layer crossfade + 5s auto-rotation + manual < > arrow navigation)
document.querySelectorAll('[data-hero-showcase]').forEach(function (showcase) {
    let slides = [];
    try {
        slides = JSON.parse(showcase.dataset.slides || '[]');
    } catch (e) {}
    if (!slides.length) return;

    // Preload all slide images into browser cache for zero-delay instant decode
    slides.forEach(function (s) {
        if (s.img) {
            const pre = new Image();
            pre.src = s.img;
        }
    });

    let layerCurrent = showcase.querySelector('#hero-layer-a');
    let layerNext = showcase.querySelector('#hero-layer-b');
    const title = showcase.querySelector('#hero-showcase-title');
    const sub = showcase.querySelector('#hero-showcase-sub');
    const captionBox = showcase.querySelector('#hero-caption-content');
    const btnPrev = showcase.querySelector('[data-hero-prev]');
    const btnNext = showcase.querySelector('[data-hero-next]');
    let currentIndex = 0;
    let timer = null;
    let isTransitioning = false;

    function showSlide(index) {
        if (isTransitioning) return;
        currentIndex = (index + slides.length) % slides.length;
        const slide = slides[currentIndex];
        if (!slide) return;

        isTransitioning = true;

        if (captionBox) {
            captionBox.classList.add('is-changing');
        }

        if (layerNext && layerCurrent) {
            layerNext.src = slide.img;
            if (slide.alt) layerNext.alt = slide.alt;

            requestAnimationFrame(() => {
                layerNext.classList.remove('is-fading-out');
                layerNext.classList.add('is-active');

                layerCurrent.classList.remove('is-active');
                layerCurrent.classList.add('is-fading-out');

                setTimeout(() => {
                    if (title) title.textContent = slide.title;
                    if (sub) sub.textContent = slide.sub;
                    if (captionBox) captionBox.classList.remove('is-changing');
                }, 200);

                setTimeout(() => {
                    layerCurrent.classList.remove('is-fading-out');
                    const temp = layerCurrent;
                    layerCurrent = layerNext;
                    layerNext = temp;
                    isTransitioning = false;
                }, 800);
            });
        } else {
            if (title) title.textContent = slide.title;
            if (sub) sub.textContent = slide.sub;
            isTransitioning = false;
        }
    }

    function nextSlide() {
        showSlide(currentIndex + 1);
    }

    function prevSlide() {
        showSlide(currentIndex - 1);
    }

    btnNext?.addEventListener('click', function (e) {
        e.preventDefault();
        nextSlide();
        resetTimer();
    });

    btnPrev?.addEventListener('click', function (e) {
        e.preventDefault();
        prevSlide();
        resetTimer();
    });

    function startTimer() {
        if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;
        stopTimer();
        timer = setInterval(nextSlide, 5000);
    }

    function stopTimer() {
        if (timer) {
            clearInterval(timer);
            timer = null;
        }
    }

    function resetTimer() {
        stopTimer();
        startTimer();
    }

    showcase.addEventListener('mouseenter', stopTimer);
    showcase.addEventListener('mouseleave', startTimer);
    showcase.addEventListener('focusin', stopTimer);
    showcase.addEventListener('focusout', startTimer);

    startTimer();
});
document.addEventListener('change', clearConstraintMessage);
