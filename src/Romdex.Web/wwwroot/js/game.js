// A game's page: whether its summary runs past the lines shown at first. Its rows that scroll sideways are carousel.js's.
window.romdexGame = {
    language: () => navigator.language,
    overflows: element => !!element && element.scrollHeight > element.clientHeight + 2,
};
