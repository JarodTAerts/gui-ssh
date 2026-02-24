// Drag and resize interop for desktop windows.
// Handles pointermove/pointerup on document for both window dragging and resizing.

let activeDrag = null;
let activeResize = null;

// --- Window Dragging ---

export function startDrag(dotNetRef, startX, startY, windowX, windowY) {
    activeDrag = { dotNetRef, startX, startY, windowX, windowY };

    document.addEventListener('pointermove', onDragMove);
    document.addEventListener('pointerup', onDragUp);
}

function onDragMove(e) {
    if (!activeDrag) return;
    e.preventDefault();

    const dx = e.clientX - activeDrag.startX;
    const dy = e.clientY - activeDrag.startY;
    const newX = Math.max(0, activeDrag.windowX + dx);
    const newY = Math.max(0, activeDrag.windowY + dy);

    activeDrag.dotNetRef.invokeMethodAsync('OnDragMove', newX, newY);
}

function onDragUp(e) {
    if (!activeDrag) return;

    activeDrag.dotNetRef.invokeMethodAsync('OnDragEnd');
    activeDrag = null;

    document.removeEventListener('pointermove', onDragMove);
    document.removeEventListener('pointerup', onDragUp);
}

// --- Window Resizing ---

export function startResize(dotNetRef, startX, startY, startWidth, startHeight, direction) {
    activeResize = { dotNetRef, startX, startY, startWidth, startHeight, direction };

    document.addEventListener('pointermove', onResizeMove);
    document.addEventListener('pointerup', onResizeUp);
}

function onResizeMove(e) {
    if (!activeResize) return;
    e.preventDefault();

    const dx = e.clientX - activeResize.startX;
    const dy = e.clientY - activeResize.startY;
    const dir = activeResize.direction;

    let newWidth = activeResize.startWidth;
    let newHeight = activeResize.startHeight;

    if (dir.includes('e')) newWidth = Math.max(300, activeResize.startWidth + dx);
    if (dir.includes('s')) newHeight = Math.max(200, activeResize.startHeight + dy);

    activeResize.dotNetRef.invokeMethodAsync('OnResizeMove', newWidth, newHeight);
}

function onResizeUp(e) {
    if (!activeResize) return;

    activeResize.dotNetRef.invokeMethodAsync('OnResizeEnd');
    activeResize = null;

    document.removeEventListener('pointermove', onResizeMove);
    document.removeEventListener('pointerup', onResizeUp);
}
