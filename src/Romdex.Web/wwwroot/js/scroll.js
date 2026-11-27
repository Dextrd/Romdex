// Where the page is scrolled to, so a page can be shown again as it was left. The page may still be filling in (covers,
// sections drawn after the first), so going back keeps trying for a moment until it's long enough to get there.
window.romdexScroll = {
    // What scrolls the page: its own box below the top bar on a computer, the window on a phone (see .page-scroll).
    scroller: () => {
        const box = document.querySelector('.page-scroll');
        return box && getComputedStyle(box).overflowY === 'auto' ? box : document.scrollingElement;
    },
    get: () => window.romdexScroll.scroller().scrollTop,
    // Calls MoreAsync on the page when a long list's end comes near the screen. The page watches its end again each
    // time it's drawn, so while the end stays near and more keeps coming, more is asked for.
    atEnd: (end, page) => {
        window.romdexScroll.endObserver?.disconnect();
        if (!end)
            return;
        const observer = new IntersectionObserver(entries => {
            if (!entries.some(e => e.isIntersecting))
                return;
            observer.disconnect();
            page.invokeMethodAsync('MoreAsync').catch(() => { });
        }, { rootMargin: '0px 0px 600px 0px' });
        window.romdexScroll.endObserver = observer;
        observer.observe(end);
    },
    to: y => {
        let tries = 0;
        const go = () => {
            const scroller = window.romdexScroll.scroller();
            scroller.scrollTo({ top: y, behavior: 'instant' });
            if (Math.abs(scroller.scrollTop - y) > 2 && ++tries < 20)
                setTimeout(go, 50);
        };
        go();
    },
    // Something the page is getting on with, such as the list an import is matching: brought into view, unless the
    // page has been scrolled by hand in the last little while.
    follow: id => {
        // After the list before has had time to shrink shut, so it isn't aimed at a moving target.
        setTimeout(() => {
            const element = document.getElementById(id);
            if (!element || Date.now() - window.romdexScroll.touched < 8000)
                return;
            const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
            element.scrollIntoView({ block: 'start', behavior: reduce ? 'instant' : 'smooth' });
        }, 280);
    },
    touched: 0,
    // A link's landing: the element it names (by its id, or the first a selector such as ".review-item" finds), once
    // the page has drawn it, scrolled to the middle of the screen and flashed so it stands out. Gives up after a few
    // seconds if it never comes.
    land: id => {
        const started = Date.now();
        const go = () => {
            const element = id.startsWith('.') ? document.querySelector(id) : document.getElementById(id);
            if (!element) {
                if (Date.now() - started < 5000)
                    setTimeout(go, 100);
                return;
            }
            const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
            element.scrollIntoView({ block: 'center', behavior: reduce ? 'instant' : 'smooth' });
            element.classList.remove('landed');
            void element.offsetWidth;
            element.classList.add('landed');
            setTimeout(() => element.classList.remove('landed'), 2200);
        };
        go();
    },
    // A row of tabs that scrolls sideways on a phone: the open tab, centred where it can be.
    tabIntoView: () => {
        const tab = document.querySelector('.page-tabs .active');
        if (!tab)
            return;
        const row = tab.parentElement;
        const left = tab.getBoundingClientRect().left - row.getBoundingClientRect().left + row.scrollLeft;
        row.scrollTo({ left: left - (row.clientWidth - tab.offsetWidth) / 2, behavior: 'instant' });
    },
};

for (const name of ['wheel', 'touchmove', 'keydown'])
    window.addEventListener(name, () => window.romdexScroll.touched = Date.now(), { passive: true, capture: true });

// Blazor scrolls the window to the top as a new page opens; where the page scrolls in its own box, that box is what goes.
(() => {
    const scrollWindow = window.scrollTo.bind(window);
    window.scrollTo = (...args) => {
        const scroller = window.romdexScroll.scroller();
        if (scroller === document.scrollingElement)
            scrollWindow(...args);
        else
            scroller.scrollTo(...args);
    };
})();

// clicked in the list in the last little while.
window.romdexScanList = {
    show: (list, index, opening) => {
        if (!list)
            return;
        if (list.romdexTouched === undefined) {
            list.romdexTouched = 0;
            for (const name of ['wheel', 'touchstart', 'pointerdown', 'keydown'])
                list.addEventListener(name, () => list.romdexTouched = Date.now(), { passive: true });
        }
        if (!opening && Date.now() - list.romdexTouched < 8000)
            return;
        // Rows are one height (.scan-file); the current one goes in the middle.
        const row = 52;
        const top = Math.max(0, index * row - (list.clientHeight - row) / 2);
        if (Math.abs(list.scrollTop - top) > 2)
            list.scrollTo({ top, behavior: opening ? 'instant' : 'smooth' });
    },
};

// Back and forward put each page where it was scrolled to this session. Where a page was is noted as it's left, by a
// link or by back and forward; the browser's own attempt comes before the page is drawn, so it's left to this.
(() => {
    const positions = new Map();
    let here = location.href;
    const leave = () => positions.set(here, window.romdexScroll.get());
    history.scrollRestoration = 'manual';

    const push = history.pushState.bind(history);
    history.pushState = (...args) => {
        leave();
        push(...args);
        here = location.href;
    };
    const replace = history.replaceState.bind(history);
    history.replaceState = (...args) => {
        replace(...args);
        here = location.href;
    };
    addEventListener('popstate', () => {
        leave();
        here = location.href;
        const y = positions.get(here) ?? 0;
        setTimeout(() => window.romdexScroll.to(y), 0);
    });
})();

// Where the browser can't style a held scrollbar itself (Firefox), the box whose scrollbar is pressed is marked until
// it's let go, so its scrollbar can take the accent colour.
if (!CSS.supports('selector(::-webkit-scrollbar)')) {
    let held = null;
    addEventListener('pointerdown', e => {
        for (let box = e.target; box instanceof Element; box = box.parentElement) {
            const rect = box.getBoundingClientRect();
            const bar = box.offsetWidth - box.clientWidth - box.clientLeft * 2;
            if (bar > 0 && e.clientX >= rect.right - bar && e.clientX <= rect.right) {
                held = box === document.body || box === document.scrollingElement ? document.documentElement : box;
                held.classList.add('scrollbar-held');
                return;
            }
        }
    }, true);
    const release = () => {
        held?.classList.remove('scrollbar-held');
        held = null;
    };
    addEventListener('pointerup', release, true);
    addEventListener('pointercancel', release, true);
}

// Lists that can be reordered by dragging (marked data-reorder): Firefox only starts a drag that carries something,
// and a dragged link or picture would otherwise be dropped as a link to open.
let reordering = null;
addEventListener('dragstart', e => {
    reordering = e.target instanceof Element ? e.target.closest('[data-reorder]') : null;
    if (!reordering)
        return;
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', '');
    // Picked up by its handle, the whole row is what moves under the pointer.
    const row = e.target.closest('.drag-handle')?.closest('tr, li');
    if (row) {
        const box = row.getBoundingClientRect();
        e.dataTransfer.setDragImage(row, e.clientX - box.left, e.clientY - box.top);
    }
});

// A row dragged near the top or bottom of what the list scrolls in scrolls it, faster the nearer the edge, so a song
// can be taken further than one screen. A long list draws only the rows near the screen, so the row picked up may be
// gone by the time it's dropped, and with it the end of its drag: a drop anywhere ends it as well.
addEventListener('dragover', e => {
    if (!reordering)
        return;
    const box = scrollBox(reordering);
    const rect = box === document.scrollingElement ? { top: 0, bottom: innerHeight } : box.getBoundingClientRect();
    // On a phone the top bar and the player cover the screen's edges, so the edge is wider there.
    const edge = Math.min(box === document.scrollingElement ? 110 : 60, (rect.bottom - rect.top) / 4);
    const near = e.clientY < rect.top + edge ? e.clientY - rect.top - edge
        : e.clientY > rect.bottom - edge ? e.clientY - rect.bottom + edge : 0;
    if (near)
        box.scrollBy({ top: near / edge * 40, behavior: 'instant' });
});
for (const name of ['drop', 'dragend'])
    addEventListener(name, () => reordering = null);

// The nearest box that scrolls the list, or the page.
function scrollBox(element) {
    for (let box = element.parentElement; box && box !== document.body; box = box.parentElement) {
        const overflow = getComputedStyle(box).overflowY;
        if ((overflow === 'auto' || overflow === 'scroll') && box.scrollHeight > box.clientHeight)
            return box;
    }
    return window.romdexScroll.scroller();
}
