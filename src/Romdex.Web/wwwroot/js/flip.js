// Moves a list's rows to their new places smoothly when it's redrawn: each row with a data-flip key is measured before
// the redraw, then slides from where it was to where it is (into another section, say). A new row fades in. Nothing
// moves for people who'd rather it didn't.
window.romdexFlip = {
    before: null,
    rows: list => list ? list.querySelectorAll('[data-flip]') : [],
    measure: list => {
        const before = new Map();
        for (const row of window.romdexFlip.rows(list))
            before.set(row.dataset.flip, row.getBoundingClientRect().top);
        window.romdexFlip.before = before;
    },
    play: list => {
        const before = window.romdexFlip.before;
        window.romdexFlip.before = null;
        if (!before || before.size === 0 || matchMedia('(prefers-reduced-motion: reduce)').matches)
            return;
        for (const row of window.romdexFlip.rows(list)) {
            const was = before.get(row.dataset.flip);
            if (was === undefined) {
                row.animate([{ opacity: 0, transform: 'translateY(-.4rem)' }, { opacity: 1, transform: 'none' }], { duration: 250, easing: 'ease-out' });
                continue;
            }
            const by = was - row.getBoundingClientRect().top;
            if (Math.abs(by) >= 1)
                row.animate([{ transform: `translateY(${by}px)` }, { transform: 'none' }], { duration: 350, easing: 'ease-in-out' });
        }
    },
};
