// A row that scrolls sideways (Carousel.razor), as Melodex's do: the arrows move it most of a screenful at a time, so
// the last one seen is still in sight to go on from. It's marked with which ways there's more to see, for the arrows to
// show and the ends to fade; on a phone it's swiped.
window.romdexCarousel = {
    update: box => {
        if (!box)
            return;
        const track = box.querySelector('.carousel-track');
        const back = box.querySelector('.carousel-back');
        const on = box.querySelector('.carousel-on');
        const show = () => {
            box.classList.toggle('can-back', track.scrollLeft > 1);
            box.classList.toggle('can-on', track.scrollLeft + track.clientWidth < track.scrollWidth - 1);
        };
        if (!box.dataset.ready) {
            box.dataset.ready = '1';
            const go = way => track.scrollBy({ left: way * Math.max(track.clientWidth * 0.8, 100), behavior: 'smooth' });
            back?.addEventListener('click', () => go(-1));
            on?.addEventListener('click', () => go(1));
            track.addEventListener('scroll', show, { passive: true });
            new ResizeObserver(show).observe(track);
        }
        show();
    },
};
