// xterm.js terminal interop for Blazor
// Uses CDN-loaded xterm.js (loaded in App.razor) or dynamic ESM imports

let xtermLoaded = false;

async function ensureXtermLoaded() {
    if (xtermLoaded) return;

    // If Terminal is not globally available, try to load from CDN
    if (typeof Terminal === 'undefined') {
        // Dynamic import from CDN
        const xtermModule = await import('https://cdn.jsdelivr.net/npm/@xterm/xterm@5.5.0/+esm');
        const fitModule = await import('https://cdn.jsdelivr.net/npm/@xterm/addon-fit@0.10.0/+esm');

        window.Terminal = xtermModule.Terminal;
        window.FitAddon = fitModule.FitAddon;
    }

    xtermLoaded = true;
}

export async function createTerminal(elementId, dotnetRef) {
    await ensureXtermLoaded();

    const term = new Terminal({
        cursorBlink: true,
        fontSize: 14,
        fontFamily: '"Cascadia Code", "Fira Code", "JetBrains Mono", Menlo, Monaco, monospace',
        theme: {
            background: '#1e1e2e',
            foreground: '#cdd6f4',
            cursor: '#f5e0dc',
            selectionBackground: '#45475a',
            black: '#45475a',
            red: '#f38ba8',
            green: '#a6e3a1',
            yellow: '#f9e2af',
            blue: '#89b4fa',
            magenta: '#f5c2e7',
            cyan: '#94e2d5',
            white: '#bac2de',
            brightBlack: '#585b70',
            brightRed: '#f38ba8',
            brightGreen: '#a6e3a1',
            brightYellow: '#f9e2af',
            brightBlue: '#89b4fa',
            brightMagenta: '#f5c2e7',
            brightCyan: '#94e2d5',
            brightWhite: '#a6adc8'
        }
    });

    const fitAddon = new FitAddon();
    term.loadAddon(fitAddon);

    const el = document.getElementById(elementId);
    if (!el) {
        console.error(`Terminal element not found: ${elementId}`);
        return null;
    }

    term.open(el);
    fitAddon.fit();

    // Handle data input (user keystrokes)
    term.onData(data => {
        dotnetRef.invokeMethodAsync('OnTerminalInput', data);
    });

    // Handle resize
    term.onResize(size => {
        dotnetRef.invokeMethodAsync('OnTerminalResize', size.cols, size.rows);
    });

    // Re-fit on window resize
    const resizeObserver = new ResizeObserver(() => {
        try { fitAddon.fit(); } catch { }
    });
    resizeObserver.observe(el);

    // Store fitAddon for later use
    term._fitAddon = fitAddon;
    term._resizeObserver = resizeObserver;
    term._element = el;

    return term;
}

export function writeToTerminal(term, data) {
    if (term) {
        term.write(data);
    }
}

export function fitTerminal(term) {
    if (term && term._fitAddon) {
        term._fitAddon.fit();
    }
}

export function disposeTerminal(term) {
    if (term) {
        if (term._resizeObserver) {
            term._resizeObserver.disconnect();
        }
        term.dispose();
    }
}
