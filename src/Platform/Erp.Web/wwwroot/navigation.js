(() => {
    document.addEventListener("keydown", (event) => {
        if (event.key !== "Tab" || !window.matchMedia("(max-width: 980px)").matches) return;
        const drawer = document.querySelector(".shell.menu-open #primary-sidebar");
        if (!drawer) return;

        const controls = Array.from(drawer.querySelectorAll(
            "a[href], button:not([disabled]), summary, input:not([disabled]), select:not([disabled]), [tabindex='0']"
        )).filter((element) => {
            if (!element.getClientRects().length || getComputedStyle(element).visibility === "hidden") return false;
            // Closed details descendants can retain layout rectangles but cannot receive focus.
            for (let ancestor = element.parentElement; ancestor && ancestor !== drawer; ancestor = ancestor.parentElement) {
                if (ancestor.tagName === "DETAILS" && !ancestor.open
                    && !ancestor.querySelector(":scope > summary")?.contains(element)) return false;
            }
            return true;
        });
        if (!controls.length) return;

        const first = controls[0];
        const last = controls[controls.length - 1];
        const active = document.activeElement;
        if (!drawer.contains(active) || (!event.shiftKey && active === last)) {
            event.preventDefault();
            first.focus();
        } else if (event.shiftKey && active === first) {
            event.preventDefault();
            last.focus();
        }
    });
})();
