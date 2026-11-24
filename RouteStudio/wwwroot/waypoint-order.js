const editors = new WeakMap();

export function attach(editor, callback) {
    const tabs = [...editor.querySelectorAll('[role="tab"]')];
    const list = editor.querySelector(".waypoint-order");
    const listeners = new AbortController();
    const options = { signal: listeners.signal };
    let drag;

    editor.querySelector(".waypoint-tabs").addEventListener("keydown", event => {
        const current = tabs.indexOf(event.target);
        if (current < 0) return;

        let next;
        switch (event.key) {
            case "ArrowLeft": case "ArrowRight": next = 1 - current; break;
            case "Home": next = 0; break;
            case "End": next = 1; break;
            default: return;
        }
        event.preventDefault();
        tabs[next].focus();
        tabs[next].click();
    }, options);

    function updateTarget() {
        const { x, y, source } = drag;
        const bounds = list.getBoundingClientRect();
        const withinList = x >= bounds.left && x < bounds.right && y >= bounds.top && y < bounds.bottom;
        const row = withinList ? document.elementFromPoint(x, y)?.closest("[data-index]") : null;
        const target = row && list.contains(row) && row !== source ? row : null;
        if (target === drag.target) return;

        drag.target?.classList.remove("drop-before", "drop-after");
        drag.target = target;
        if (target) target.classList.add(Number(target.dataset.index) < drag.from ? "drop-before" : "drop-after");
    }

    function scrollList() {
        const bounds = list.getBoundingClientRect();
        const { x, y } = drag;
        const edge = 28;
        if (x >= bounds.left && x < bounds.right && y >= bounds.top && y < bounds.bottom) {
            if (y - bounds.top < edge) list.scrollTop -= 12;
            else if (bounds.bottom - y < edge) list.scrollTop += 12;
        }
        updateTarget();
        drag.frame = requestAnimationFrame(scrollList);
    }

    function cancelDrag() {
        if (!drag) return;

        const { pointer, source, target, frame } = drag;
        drag = undefined;
        cancelAnimationFrame(frame);
        source.classList.remove("dragging");
        target?.classList.remove("drop-before", "drop-after");
        if (list.hasPointerCapture(pointer)) list.releasePointerCapture(pointer);
    }

    list.addEventListener("pointerdown", event => {
        const handle = event.target.closest("[data-drag-handle]");
        if (!handle || drag || event.button !== 0 || !event.isPrimary) return;

        const source = handle.closest("[data-index]");
        list.setPointerCapture(event.pointerId);
        source.classList.add("dragging");
        drag = {
            pointer: event.pointerId, from: Number(source.dataset.index), revision: Number(list.dataset.revision),
            source, target: null, x: event.clientX, y: event.clientY, frame: requestAnimationFrame(scrollList)
        };
        event.preventDefault();
    }, options);

    list.addEventListener("pointermove", event => {
        if (event.pointerId !== drag?.pointer) return;
        drag.x = event.clientX;
        drag.y = event.clientY;
        updateTarget();
    }, options);

    list.addEventListener("pointerup", event => {
        if (event.pointerId !== drag?.pointer) return;
        drag.x = event.clientX;
        drag.y = event.clientY;
        updateTarget();

        const { from, target, revision } = drag;
        cancelDrag();
        if (target) callback.invokeMethodAsync("MoveDroppedWaypoint", from, Number(target.dataset.index), revision)
            .catch(error => { if (!listeners.signal.aborted) console.error(error); });
    }, options);

    for (const name of ["pointercancel", "lostpointercapture"]) {
        list.addEventListener(name, event => {
            if (event.pointerId === drag?.pointer) cancelDrag();
        }, options);
    }
    document.addEventListener("keydown", event => {
        if (event.key === "Escape" && drag) cancelDrag();
    }, options);

    editors.set(editor, () => {
        cancelDrag();
        listeners.abort();
    });
}

export function focusMove(editor, index, direction) {
    const row = editor.querySelector(`.waypoint-order [data-index="${index}"]`);
    const button = row?.querySelector(`[data-move="${direction}"]:enabled`) ?? row?.querySelector("[data-move]:enabled");
    button?.focus();
}

export function detach(editor) {
    editors.get(editor)?.();
    editors.delete(editor);
}
