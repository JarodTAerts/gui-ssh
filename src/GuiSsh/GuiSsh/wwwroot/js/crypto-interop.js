// Browser-side credential encryption using Web Crypto API (SubtleCrypto).
// Uses AES-GCM with a PBKDF2-derived key. All crypto stays in the browser.
// Note: Uses arrow functions bound to the object to avoid `this` context loss
// when called via Blazor JS interop (IJSRuntime).

window.cryptoInterop = (() => {
    const APP_SALT = 'GuiSsh.Credential.Store.v1';

    async function deriveKey(passphrase, salt) {
        const enc = new TextEncoder();
        const keyMaterial = await crypto.subtle.importKey(
            'raw',
            enc.encode(passphrase),
            'PBKDF2',
            false,
            ['deriveKey']
        );

        return await crypto.subtle.deriveKey(
            {
                name: 'PBKDF2',
                salt: salt,
                iterations: 100000,
                hash: 'SHA-256'
            },
            keyMaterial,
            { name: 'AES-GCM', length: 256 },
            false,
            ['encrypt', 'decrypt']
        );
    }

    async function encrypt(plaintext, passphrase) {
        const enc = new TextEncoder();
        const salt = crypto.getRandomValues(new Uint8Array(16));
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const key = await deriveKey(passphrase, salt);

        const ciphertext = await crypto.subtle.encrypt(
            { name: 'AES-GCM', iv: iv },
            key,
            enc.encode(plaintext)
        );

        const result = new Uint8Array(salt.length + iv.length + ciphertext.byteLength);
        result.set(salt, 0);
        result.set(iv, salt.length);
        result.set(new Uint8Array(ciphertext), salt.length + iv.length);

        let binary = '';
        for (let i = 0; i < result.length; i++) {
            binary += String.fromCharCode(result[i]);
        }
        return btoa(binary);
    }

    async function decrypt(ciphertext, passphrase) {
        try {
            const binary = atob(ciphertext);
            const data = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) {
                data[i] = binary.charCodeAt(i);
            }

            const salt = data.slice(0, 16);
            const iv = data.slice(16, 28);
            const encrypted = data.slice(28);

            const key = await deriveKey(passphrase, salt);

            const decrypted = await crypto.subtle.decrypt(
                { name: 'AES-GCM', iv: iv },
                key,
                encrypted
            );

            return new TextDecoder().decode(decrypted);
        } catch (e) {
            console.warn('cryptoInterop.decrypt failed:', e);
            return null;
        }
    }

    function makePassphrase(connectionId) {
        return `GuiSsh.${connectionId}.${APP_SALT}`;
    }

    return {
        encryptCredential: async function(connectionId, username, password) {
            console.log('cryptoInterop.encryptCredential called for:', connectionId);
            const payload = JSON.stringify({ u: username, p: password });
            const passphrase = makePassphrase(connectionId);
            const encrypted = await encrypt(payload, passphrase);

            // Also persist directly to localStorage as backup
            localStorage.setItem(`guissh_cred_${connectionId}`, encrypted);
            console.log('cryptoInterop.encryptCredential saved to localStorage');

            return encrypted;
        },

        decryptCredential: async function(connectionId, encryptedData) {
            console.log('cryptoInterop.decryptCredential called for:', connectionId);
            const passphrase = makePassphrase(connectionId);
            const json = await decrypt(encryptedData, passphrase);
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
