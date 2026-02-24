// Utility: observe element resize and call back to .NET
window.GuiSsh = window.GuiSsh || {};

window.GuiSsh.observeResize = function (element, dotNetRef, methodName) {
    if (!element) return;
    const obs = new ResizeObserver(entries => {
        try {
            for (const entry of entries) {
                const w = entry.contentRect.width;
                dotNetRef.invokeMethodAsync(methodName, w);
            }
        } catch (e) {
            // Component disposed — disconnect
            obs.disconnect();
        }
    });
    obs.observe(element);
    // Fire immediately with current width
    try {
        dotNetRef.invokeMethodAsync(methodName, element.offsetWidth);
    } catch (e) { /* ignore */ }
};
