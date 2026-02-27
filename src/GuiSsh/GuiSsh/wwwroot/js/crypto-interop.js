// Browser-side credential encryption using Web Crypto API.
// Uses a non-extractable AES-GCM 256-bit key stored in IndexedDB.
// The key material can NEVER be read by JavaScript — only used for encrypt/decrypt operations.
// This protects against localStorage scraping, extension snooping, and offline attacks.
//
// Note: Clearing browser data (IndexedDB) destroys the key. Saved credentials will need
// to be re-entered. Credentials do not roam across browsers or devices.

window.cryptoInterop = (() => {
    const DB_NAME = 'guissh-keystore';
    const DB_VERSION = 1;
    const STORE_NAME = 'keys';
    const KEY_ID = 'credential-master-key';

    // --- IndexedDB helpers ---

    function openDb() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = () => {
                const db = req.result;
                if (!db.objectStoreNames.contains(STORE_NAME))
                    db.createObjectStore(STORE_NAME);
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    async function getStoredKey() {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readonly');
            const store = tx.objectStore(STORE_NAME);
            const req = store.get(KEY_ID);
            req.onsuccess = () => resolve(req.result ?? null);
            req.onerror = () => reject(req.error);
        });
    }

    async function storeKey(key) {
        const db = await openDb();
        return new Promise((resolve, reject) => {
            const tx = db.transaction(STORE_NAME, 'readwrite');
            const store = tx.objectStore(STORE_NAME);
            const req = store.put(key, KEY_ID);
            req.onsuccess = () => resolve();
            req.onerror = () => reject(req.error);
        });
    }

    // --- Key management ---

    let _cachedKey = null;

    async function getOrCreateKey() {
        if (_cachedKey) return _cachedKey;

        let key = await getStoredKey();
        if (key) {
            _cachedKey = key;
            return key;
        }

        // Generate a new non-extractable AES-GCM 256-bit key
        key = await crypto.subtle.generateKey(
            { name: 'AES-GCM', length: 256 },
            false,    // NON-EXTRACTABLE: key material can never be exported
            ['encrypt', 'decrypt']
        );

        await storeKey(key);
        _cachedKey = key;
        return key;
    }

    // --- Encrypt / Decrypt ---

    async function encrypt(plaintext) {
        const key = await getOrCreateKey();
        const enc = new TextEncoder();
        const iv = crypto.getRandomValues(new Uint8Array(12));

        const ciphertext = await crypto.subtle.encrypt(
            { name: 'AES-GCM', iv },
            key,
            enc.encode(plaintext)
        );

        // Prepend IV to ciphertext
        const result = new Uint8Array(iv.length + ciphertext.byteLength);
        result.set(iv, 0);
        result.set(new Uint8Array(ciphertext), iv.length);

        // Encode as base64
        let binary = '';
        for (let i = 0; i < result.length; i++)
            binary += String.fromCharCode(result[i]);
        return btoa(binary);
    }

    async function decrypt(base64Data) {
        try {
            const key = await getOrCreateKey();
            const binary = atob(base64Data);
            const data = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++)
                data[i] = binary.charCodeAt(i);

            const iv = data.slice(0, 12);
            const encrypted = data.slice(12);

            const decrypted = await crypto.subtle.decrypt(
                { name: 'AES-GCM', iv },
                key,
                encrypted
            );

            return new TextDecoder().decode(decrypted);
        } catch (e) {
            console.warn('cryptoInterop.decrypt failed:', e);
            return null;
        }
    }

    // --- Public API (interface unchanged for Blazor interop) ---

    return {
        encryptCredential: async function (connectionId, username, password) {
            const payload = JSON.stringify({ u: username, p: password });
            const encrypted = await encrypt(payload);

            // Persist to localStorage
            localStorage.setItem(`guissh_cred_${connectionId}`, encrypted);

            return encrypted;
        },

        decryptCredential: async function (connectionId, encryptedData) {
            const json = await decrypt(encryptedData);
            if (!json) return null;
            try {
                const obj = JSON.parse(json);
                return { username: obj.u, password: obj.p };
            } catch (e) {
                console.warn('cryptoInterop.decryptCredential JSON parse failed:', e);
                return null;
            }
        }
    };
})();
