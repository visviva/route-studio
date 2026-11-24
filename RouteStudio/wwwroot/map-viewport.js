const views = new WeakMap();
const full = { x: 0, y: 0, width: 1, height: 1 };
const clamp = (value, min, max) => Math.max(min, Math.min(value, max));
const percent = value => value * 100 + "%";

export function setMap(figure, aspect, cropHint, mapWidth) {
    let state = views.get(figure);
    if (!state) {
        state = attach(figure, cropHint, mapWidth);
        views.set(figure, state);
    }
    state.reset(aspect);
}

export function detach(figure) {
    views.get(figure)?.abort();
    views.delete(figure);
}

function attach(figure, cropHint, mapWidth) {
    const viewport = figure.querySelector(".map-viewport");
    const selection = figure.querySelector(".map-selection");
    const note = figure.querySelector("[data-map-note]");
    const buttons = Object.fromEntries([...figure.querySelectorAll("[data-map-action]")]
        .map(button => [button.dataset.mapAction, button]));
    const listeners = new AbortController();
    const options = { signal: listeners.signal };
    const formatWidth = new Intl.NumberFormat(document.documentElement.lang, { style: "percent" });
    let aspect = 1;
    let crop = full;
    let view = full;
    let selecting = false;
    let drag;

    function show() {
        viewport.style.setProperty("--view-ratio", aspect * view.width / view.height);
        viewport.style.setProperty("--map-width", percent(1 / view.width));
        viewport.style.setProperty("--map-left", percent(-view.x / view.width));
        viewport.style.setProperty("--map-top", percent(-view.y / view.height));
        const zoom = crop.width / view.width;
        viewport.classList.toggle("can-pan", zoom > 1.0001);
        viewport.classList.toggle("can-crop", selecting);
        buttons.in.disabled = zoom >= 8 - 0.0001;
        buttons.out.disabled = zoom <= 1.0001;
        buttons.reset.disabled = crop === full && buttons.out.disabled && !selecting;
        buttons.crop.setAttribute("aria-pressed", String(selecting));
        note.textContent = selecting ? cropHint
            : crop === full && buttons.out.disabled ? "" : mapWidth.replace("{0}", formatWidth.format(1 / view.width));
    }

    function place(x, y, width, height) {
        view = {
            x: clamp(x, crop.x, crop.x + crop.width - width),
            y: clamp(y, crop.y, crop.y + crop.height - height),
            width, height
        };
        show();
    }

    function zoomBy(scale, anchor = { x: 0.5, y: 0.5 }) {
        const width = clamp(view.width * scale, crop.width / 8, crop.width);
        const height = width * crop.height / crop.width;
        place(view.x + anchor.x * (view.width - width), view.y + anchor.y * (view.height - height), width, height);
    }

    function cancelDrag() {
        if (drag && viewport.hasPointerCapture(drag.id)) viewport.releasePointerCapture(drag.id);
        drag = undefined;
        selection.hidden = true;
    }

    function reset(nextAspect = aspect) {
        cancelDrag();
        aspect = nextAspect;
        view = crop = full;
        selecting = false;
        show();
    }

    function cursor(event) {
        const rect = viewport.getBoundingClientRect();
        return { x: clamp((event.clientX - rect.left) / rect.width, 0, 1),
            y: clamp((event.clientY - rect.top) / rect.height, 0, 1) };
    }

    function rectangle(a, b) {
        return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y),
            width: Math.abs(a.x - b.x), height: Math.abs(a.y - b.y) };
    }

    figure.addEventListener("click", event => {
        const action = event.target.closest("[data-map-action]")?.dataset.mapAction;
        switch (action) {
            case "in": zoomBy(0.8); break;
            case "out": zoomBy(1.25); break;
            case "reset": reset(); break;
            case "crop":
                cancelDrag();
                selecting = !selecting;
                show();
                if (selecting) viewport.focus({ preventScroll: true });
                break;
        }
    }, options);

    viewport.addEventListener("wheel", event => {
        if (selecting || (!event.ctrlKey && document.activeElement !== viewport)) return;
        event.preventDefault();
        zoomBy(event.deltaY < 0 ? 0.8 : 1.25, cursor(event));
    }, { ...options, passive: false });

    viewport.addEventListener("keydown", event => {
        if (event.key === "Escape" && selecting) {
            cancelDrag();
            selecting = false;
            show();
            event.preventDefault();
            return;
        }
        switch (event.key) {
            case "+": case "=": zoomBy(0.8); break;
            case "-": zoomBy(1.25); break;
            case "ArrowLeft": place(view.x - view.width / 8, view.y, view.width, view.height); break;
            case "ArrowRight": place(view.x + view.width / 8, view.y, view.width, view.height); break;
            case "ArrowUp": place(view.x, view.y - view.height / 8, view.width, view.height); break;
            case "ArrowDown": place(view.x, view.y + view.height / 8, view.width, view.height); break;
            default: return;
        }
        event.preventDefault();
    }, options);

    viewport.addEventListener("pointerdown", event => {
        if (event.button !== 0 || drag || (!selecting && !viewport.classList.contains("can-pan"))) return;
        viewport.focus({ preventScroll: true });
        drag = { id: event.pointerId, start: cursor(event), view };
        viewport.setPointerCapture(drag.id);
        if (selecting) {
            Object.assign(selection.style, { left: percent(drag.start.x), top: percent(drag.start.y), width: "0", height: "0" });
            selection.hidden = false;
        }
    }, options);

    viewport.addEventListener("pointermove", event => {
        if (drag?.id !== event.pointerId) return;
        const point = cursor(event);
        if (selecting) {
            const rect = rectangle(drag.start, point);
            Object.assign(selection.style, { left: percent(rect.x), top: percent(rect.y),
                width: percent(rect.width), height: percent(rect.height) });
        } else {
            const start = drag.view;
            place(start.x - (point.x - drag.start.x) * start.width, start.y - (point.y - drag.start.y) * start.height,
                start.width, start.height);
        }
    }, options);

    viewport.addEventListener("pointerup", event => {
        if (drag?.id !== event.pointerId) return;
        const rect = rectangle(drag.start, cursor(event));
        cancelDrag();
        if (!selecting || rect.width * viewport.clientWidth < 12 || rect.height * viewport.clientHeight < 12) return;
        crop = {
            x: view.x + rect.x * view.width,
            y: view.y + rect.y * view.height,
            width: rect.width * view.width,
            height: rect.height * view.height
        };
        selecting = false;
        view = crop;
        show();
    }, options);

    viewport.addEventListener("pointercancel", cancelDrag, options);
    return { reset, abort: () => { cancelDrag(); listeners.abort(); } };
}
