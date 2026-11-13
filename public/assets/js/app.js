/* dig - small progressive enhancements. The pages work without this file. */
(function () {
    "use strict";

    var THEMES = ["light", "dark", "oled"];

    function currentTheme() {
        return document.documentElement.getAttribute("data-theme") || "dark";
    }

    function setTheme(name) {
        document.documentElement.setAttribute("data-theme", name);
        try { localStorage.setItem("dig-theme", name); } catch (e) {}
        var meta = document.querySelector('meta[name="theme-color"]');
        if (meta) meta.setAttribute("content", name === "light" ? "#ffffff" : (name === "oled" ? "#000000" : "#101418"));
    }

    function initThemeToggle() {
        var button = document.getElementById("theme-toggle");
        if (!button) return;
        button.addEventListener("click", function () {
            var index = THEMES.indexOf(currentTheme());
            setTheme(THEMES[(index + 1) % THEMES.length]);
        });
    }

    function copyText(text, button) {
        function done(ok) {
            if (!button) return;
            var original = button.getAttribute("data-label") || button.textContent;
            button.setAttribute("data-label", original);
            button.textContent = ok ? (button.getAttribute("data-copied") || "copied") : "failed";
            button.classList.toggle("done", ok);
            setTimeout(function () {
                button.textContent = original;
                button.classList.remove("done");
            }, 1400);
        }

        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(text).then(function () { done(true); }, function () { done(fallback(text)); });
            return;
        }
        done(fallback(text));
    }

    function fallback(text) {
        try {
            var area = document.createElement("textarea");
            area.value = text;
            area.setAttribute("readonly", "");
            area.style.position = "fixed";
            area.style.opacity = "0";
            document.body.appendChild(area);
            area.select();
            var ok = document.execCommand("copy");
            document.body.removeChild(area);
            return ok;
        } catch (e) {
            return false;
        }
    }

    function initCopyButtons() {
        document.addEventListener("click", function (event) {
            var target = event.target;
            while (target && target !== document.body && !target.hasAttribute("data-copy")) target = target.parentNode;
            if (!target || target === document.body) return;
            event.preventDefault();
            copyText(target.getAttribute("data-copy") || "", target);
        });
    }

    function initHotkeys() {
        document.addEventListener("keydown", function (event) {
            if (event.key !== "/" || event.metaKey || event.ctrlKey || event.altKey) return;
            var tag = (event.target && event.target.tagName || "").toLowerCase();
            if (tag === "input" || tag === "textarea") return;
            var input = document.querySelector('input[type="search"]');
            if (input) { event.preventDefault(); input.focus(); input.select(); }
        });
    }

    function initLiveStats() {
        var nodes = document.querySelectorAll("[data-live]");
        if (!nodes.length) return;
        var meta = document.querySelector('meta[name="dig-api"]');
        var api = meta ? meta.getAttribute("content") : "/api/";
        var timer = setInterval(function () {
            if (document.hidden) return;
            fetch(api + "stats", { headers: { "Accept": "application/json" } })
                .then(function (r) { return r.ok ? r.json() : null; })
                .then(function (data) {
                    if (!data) return;
                    for (var i = 0; i < nodes.length; i++) {
                        var key = nodes[i].getAttribute("data-live");
                        var value = data;
                        key.split(".").forEach(function (part) { value = value ? value[part] : null; });
                        if (value === null || value === undefined) continue;
                        nodes[i].textContent = String(value);
                    }
                })
                .catch(function () {});
        }, 15000);
        window.addEventListener("pagehide", function () { clearInterval(timer); });
    }

    function ready(fn) {
        if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", fn);
        else fn();
    }

    ready(function () {
        initThemeToggle();
        initCopyButtons();
        initHotkeys();
        initLiveStats();
    });
})();
