// Copying text for the copy buttons. Browsers only offer navigator.clipboard on secure pages (https, or localhost), so
// over plain http, such as a home server's own address, the text is put in a hidden box and copied the older way. The
// box goes beside the button pressed, so a dialog that keeps focus to itself doesn't stop it being selected.
window.romdexCopy = async function (text) {
    if (window.isSecureContext && navigator.clipboard) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            // Refused, perhaps by a permission: try the older way.
        }
    }
    const pressed = document.activeElement;
    const box = document.createElement("textarea");
    box.value = text;
    box.setAttribute("readonly", "");
    box.style.cssText = "position:fixed;top:0;left:0;width:1px;height:1px;opacity:0;";
    (pressed?.parentElement ?? document.body).appendChild(box);
    box.select();
    let copied = false;
    try {
        copied = document.execCommand("copy");
    } catch {
        copied = false;
    }
    box.remove();
    pressed?.focus?.();
    return copied;
};
