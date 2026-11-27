// A download's attempts, shown one at a time (RequestDetail.razor): the arrows step through them, and the row scrolls
// sideways too. It opens on the latest, and follows a new one in if the latest was showing.
window.romdexAttempts = {
    update: box => {
        if (!box)
            return;
        const track = box.querySelector('.attempts-track');
        const earlier = box.querySelector('.attempts-earlier');
        const later = box.querySelector('.attempts-later');
        const count = box.querySelector('.attempts-count span');
        const at = () => Math.round(track.scrollLeft / Math.max(track.clientWidth, 1));
        const show = () => {
            const n = track.children.length, i = Math.min(at(), n - 1);
            count.textContent = i + 1;
            earlier.disabled = i <= 0;
            later.disabled = i >= n - 1;
        };
        const go = i => track.scrollTo({ left: i * track.clientWidth, behavior: 'smooth' });
        const n = track.children.length;
        if (!box.dataset.ready) {
            box.dataset.ready = '1';
            earlier.addEventListener('click', () => go(at() - 1));
            later.addEventListener('click', () => go(at() + 1));
            track.addEventListener('scroll', show, { passive: true });
            box.dataset.count = n;
            track.scrollTo({ left: track.scrollWidth, behavior: 'instant' });
        } else if (+box.dataset.count !== n) {
            const wasLatest = at() >= +box.dataset.count - 1;
            box.dataset.count = n;
            if (wasLatest)
                track.scrollTo({ left: track.scrollWidth, behavior: 'instant' });
        }
        show();
    },
};
