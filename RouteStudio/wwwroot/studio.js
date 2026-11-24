window.routeStudio = {
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
