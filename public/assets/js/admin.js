/* Admin dashboard: polls /api/stats and /api/log, runs the action buttons. */
(function () {
    "use strict";

    var lastSeq = 0;
    var logBox = document.getElementById("log");
    var result = document.getElementById("action-result");
    var dot = document.getElementById("live-dot");

    function number(value) {
        if (value === null || value === undefined) return "\u2014";
        if (value < 1000) return String(value);
        if (value < 1000000) return (value / 1000).toFixed(1) + "k";
        return (value / 1000000).toFixed(1) + "M";
    }

    function set(id, value) {
        var el = document.getElementById(id);
        if (el) el.textContent = value;
    }

    function pollStats() {
        fetch("api/stats", { headers: { "Accept": "application/json" } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                set("v-torrents", number(data.torrents));
                set("v-nodes", number(data.dht ? data.dht.nodes : 0));
                set("v-harvested", number(data.crawler ? data.crawler.harvested : 0));
                set("v-fetched", number(data.crawler ? data.crawler.fetched : 0));
                if (dot) dot.className = "dot " + (data.crawler && data.crawler.paused ? "off" : "on");
            })
            .catch(function () {});
    }

    function appendLog(entries) {
        if (!logBox || !entries || !entries.length) return;
        var atBottom = logBox.scrollTop + logBox.clientHeight >= logBox.scrollHeight - 30;
        var fragment = document.createDocumentFragment();
        entries.forEach(function (e) {
            var line = document.createElement("div");
            line.className = e.level.toLowerCase();
            line.textContent = e.time + " [" + e.level + "] " + (e.cat || "") + " " + e.msg;
            fragment.appendChild(line);
        });
        logBox.appendChild(fragment);
        while (logBox.childNodes.length > 400) logBox.removeChild(logBox.firstChild);
        if (atBottom) logBox.scrollTop = logBox.scrollHeight;
    }

    function pollLog() {
        fetch("api/log?lines=120&since=" + lastSeq, { headers: { "Accept": "application/json" } })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data || !data.entries) return;
                appendLog(data.entries);
                if (data.last) lastSeq = data.last;
            })
            .catch(function () {});
    }

    function runAction(button) {
        var url = button.getAttribute("data-action");
        if (!url) return;
        button.disabled = true;
        fetch(url, { method: "POST", headers: { "Accept": "application/json" } })
            .then(function (r) { return r.json(); })
            .then(function (data) {
                if (result) result.textContent = (data && data.message) ? data.message : "done";
                pollStats();
                pollLog();
            })
            .catch(function () { if (result) result.textContent = "request failed"; })
            .then(function () { button.disabled = false; });
    }

    function init() {
        pollStats();
        pollLog();
        setInterval(pollStats, 3000);
        setInterval(pollLog, 3000);

        var actions = document.querySelectorAll("[data-action]");
        for (var i = 0; i < actions.length; i++) {
            actions[i].addEventListener("click", function () { runAction(this); });
        }

        var clear = document.getElementById("log-clear");
        if (clear && logBox) clear.addEventListener("click", function () { logBox.textContent = ""; });

        var toggle = document.getElementById("theme-toggle");
        if (toggle) {
            var themes = ["light", "dark", "oled"];
            toggle.addEventListener("click", function () {
                var now = document.documentElement.getAttribute("data-theme") || "dark";
                var next = themes[(themes.indexOf(now) + 1) % themes.length];
                document.documentElement.setAttribute("data-theme", next);
                try { localStorage.setItem("dig-theme", next); } catch (e) {}
            });
        }
    }

    if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", init);
    else init();
})();
