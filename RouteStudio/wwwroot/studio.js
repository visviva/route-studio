(() => {
    const preferences = navigator.languages?.length ? navigator.languages : [navigator.language];
    const browserLanguage = preferences
        .map(tag => tag?.toLowerCase().split("-")[0])
        .find(code => Object.hasOwn(routeStudioText, code)) ?? "en";
    const storageKey = "route-studio-language";
    let savedPreference;
    try { savedPreference = localStorage.getItem(storageKey); }
    catch { /* Language selection still works for the current page. */ }
    let localization = applyLanguage(savedPreference);

    function applyLanguage(selection) {
        const preference = Object.hasOwn(routeStudioText, selection) ? selection : "";
        const language = preference || browserLanguage;
        const messages = { ...routeStudioText.en, ...routeStudioText[language] };

        document.documentElement.lang = language;
        document.querySelector('meta[name="description"]').content = messages.Description;
        for (const element of document.querySelectorAll("[data-text]")) {
            element.textContent = messages[element.dataset.text];
        }

        return { language, preference, messages };
    }

    window.routeStudio = {
        getLocalization: () => localization,
        setLanguage(selection) {
            localization = applyLanguage(selection);
            try {
                if (localization.preference) localStorage.setItem(storageKey, localization.preference);
                else localStorage.removeItem(storageKey);
            } catch { /* Language selection still works for the current page. */ }
            document.dispatchEvent(new Event("route-studio-language-change"));
            return localization;
        },
        async copyExport(output) {
            try {
                await navigator.clipboard.writeText(output.value);
                return true;
            } catch {
                output.focus();
                output.select();
                return false;
            }
        }
    };

    Blazor.start({ applicationCulture: localization.language }).catch(error => {
        document.getElementById("blazor-error-ui").style.display = "block";
        console.error(error);
    });
})();
