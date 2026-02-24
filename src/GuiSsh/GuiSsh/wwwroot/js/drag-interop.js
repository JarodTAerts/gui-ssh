// Drag interop for desktop windows.
// Handles pointermove/pointerup on document to track window dragging.

let activeDrag = null;

export function startDrag(dotNetRef, startX, startY, windowX, windowY) {
    activeDrag = { dotNetRef, startX, startY, windowX, windowY };

    document.addEventListener('pointermove', onPointerMove);
    document.addEventListener('pointerup', onPointerUp);
}

function onPointerMove(e) {
    if (!activeDrag) return;

    const dx = e.clientX - activeDrag.startX;
    const dy = e.clientY - activeDrag.startY;
    const newX = Math.max(0, activeDrag.windowX + dx);
    const newY = Math.max(0, activeDrag.windowY + dy);

    activeDrag.dotNetRef.invokeMethodAsync('OnDragMove', newX, newY);
}

function onPointerUp(e) {
    if (!activeDrag) return;

    activeDrag.dotNetRef.invokeMethodAsync('OnDragEnd');
    activeDrag = null;

    document.removeEventListener('pointermove', onPointerMove);
    document.removeEventListener('pointerup', onPointerUp);
}
