window.startrekSqlAssistant = {
    scrollToBottom: function (element) {
        if (element) {
            element.scrollTop = element.scrollHeight;
        }
    }
};

// Buttons inside #components-reconnect-modal (see App.razor). blazor.web.js
// only toggles state classes on the modal; acting on them is up to the page.
// Blazor.reconnect() / Blazor.resumeCircuit() resolve false when the server
// has discarded the circuit, and there is nothing left to rejoin, so fall back
// to a reload. On success the framework hides the modal itself.
document.addEventListener("click", async function (event) {
    const button = event.target.closest("#components-reconnect-modal [data-reconnect-action]");
    if (!button) {
        return;
    }

    const action = button.dataset.reconnectAction;
    if (action === "reload") {
        location.reload();
        return;
    }

    button.disabled = true;
    try {
        const ok = action === "resume"
            ? await Blazor.resumeCircuit()
            : await Blazor.reconnect();
        if (!ok) {
            location.reload();
        }
    } catch {
        location.reload();
    } finally {
        button.disabled = false;
    }
});
