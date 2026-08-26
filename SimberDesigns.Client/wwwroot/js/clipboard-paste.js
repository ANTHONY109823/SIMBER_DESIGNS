window.initializeClipboardPaste = (dotnetRef) => {
    if (window.__simberPasteBound) {
        return;
    }
    window.__simberPasteBound = true;
    document.addEventListener("paste", async (event) => {
        const items = event.clipboardData && event.clipboardData.items;
        if (!items) {
            return;
        }
        const imageItem = Array.from(items).find((item) => item.type && item.type.startsWith("image/"));
        if (!imageItem) {
            return;
        }
        event.preventDefault();
        const blob = imageItem.getAsFile();
        if (!blob) {
            return;
        }
        const reader = new FileReader();
        reader.onload = () => dotnetRef.invokeMethodAsync("OnPasteImage", reader.result);
        reader.readAsDataURL(blob);
    });
};
