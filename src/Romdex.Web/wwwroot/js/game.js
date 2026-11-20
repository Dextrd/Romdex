// A game's page: whether its summary runs past the lines shown at first, and its rows of reviews, pictures and the rest
// of its series, which scroll sideways by a swipe or their arrows and fade out at an edge with more past it.
window.romdexGame = {
    language: () => navigator.language,
    overflows: element => !!element && element.scrollHeight > element.clientHeight + 2,
    carousels: () => document.querySelectorAll('.carousel').forEach(window.romdexGame.carousel),
    carousel: carousel => {
        const track = carousel?.querySelector('.carousel-track');
        if (!track)
            return;
        const update = () => {
            carousel.classList.toggle('at-start', track.scrollLeft <= 4);
            carousel.classList.toggle('at-end', track.scrollLeft + track.clientWidth >= track.scrollWidth - 4);
        };
        if (!carousel.romdexCarousel) {
            carousel.romdexCarousel = true;
            // Most of a width at a time, so the last one seen is still in sight to go on from.
            carousel.addEventListener('click', e => {
                const arrow = e.target instanceof Element ? e.target.closest('.carousel-arrow') : null;
                if (arrow)
                    track.scrollBy({ left: Number(arrow.dataset.step) * track.clientWidth * 0.8, behavior: 'smooth' });
            });
            track.addEventListener('scroll', update, { passive: true });
            new ResizeObserver(update).observe(track);
        }
        update();
    },
};
