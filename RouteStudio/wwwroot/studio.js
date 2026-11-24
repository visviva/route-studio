(() => {
    const preferences = navigator.languages?.length ? navigator.languages : [navigator.language];
    const language = preferences
        .map(tag => tag?.toLowerCase().split("-")[0])
        .find(code => Object.hasOwn(routeStudioText, code)) ?? "en";
    const messages = { ...routeStudioText.en, ...routeStudioText[language] };

    document.documentElement.lang = language;
    document.querySelector('meta[name="description"]').content = messages.Description;
    for (const element of document.querySelectorAll("[data-text]")) {
        element.textContent = messages[element.dataset.text];
    }

    window.routeStudio = {
        getMessages: () => messages,
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

    Blazor.start({ applicationCulture: language }).catch(error => {
        document.getElementById("blazor-error-ui").style.display = "block";
        console.error(error);
    });
})();
