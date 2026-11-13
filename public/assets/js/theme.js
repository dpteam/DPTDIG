/* Applies the saved theme before the first paint. Loaded synchronously in <head>. */
(function () {
    try {
        var saved = localStorage.getItem("dig-theme");
        if (saved === "light" || saved === "dark" || saved === "oled") {
            document.documentElement.setAttribute("data-theme", saved);
        }
    } catch (e) { /* private mode: keep the server default */ }
})();
