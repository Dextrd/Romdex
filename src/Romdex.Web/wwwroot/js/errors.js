// The banner Blazor shows when the app hits an error it couldn't handle: what the error was, its details to read or
// copy, and the same sent to the server's log, once for each different error.
window.romdexError = (() => {
    const sent = new Set();
    let details = "";

    function show(message, more) {
        details = more || message;
        const banner = document.getElementById("blazor-error-ui");
        if (banner) {
            banner.querySelector(".error-message").textContent = message;
            banner.querySelector(".error-details").textContent = details;
        }
        if (sent.has(message) || sent.size >= 10)
            return;
        sent.add(message);
        fetch("api/v1/client-errors", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ message, details, page: location.pathname + location.search }),
        }).catch(() => { });
    }

    document.addEventListener("click", e => {
        const banner = document.getElementById("blazor-error-ui");
        if (!banner || !banner.contains(e.target))
            return;
        if (e.target.closest(".error-toggle")) {
            const open = banner.classList.toggle("open");
            e.target.closest(".error-toggle").setAttribute("aria-expanded", String(open));
        } else if (e.target.closest(".error-copy")) {
            const button = e.target.closest(".error-copy");
            romdexCopy(banner.querySelector(".error-message").textContent + "\n\n" + details).then(copied => {
                if (!copied)
                    return;
                button.textContent = "Copied";
                setTimeout(() => button.textContent = "Copy", 1500);
            });
        }
    });

    return { show };
})();
