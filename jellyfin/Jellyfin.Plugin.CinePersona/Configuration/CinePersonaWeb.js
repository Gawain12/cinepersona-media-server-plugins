(function () {
    "use strict";

    // 强杀旧版本遗留的右下角悬浮球
    try {
        var legacyTrigger = document.getElementById("cinepersona-review-trigger");
        if (legacyTrigger) {
            legacyTrigger.remove();
        }
    } catch (e) {}

    if (window.__cinePersonaWebVersion === "0.3.0") {
        return;
    }
    window.__cinePersonaWebVersion = "0.3.0";
    window.__cinePersonaWebLoaded = true;

    var state = {
        itemId: null,
        item: null,
        modal: null,
        rating: 0,
        hasSpoiler: false,
        activity: null,
        settings: { Configured: false, Enabled: true },
        pollTimer: null
    };

    function apiUrl(path) {
        if (window.ApiClient && typeof ApiClient.getUrl === "function") {
            return ApiClient.getUrl(path);
        }
        return path.charAt(0) === "/" ? path : "/" + path;
    }

    function apiToken() {
        if (!window.ApiClient) {
            return "";
        }
        if (typeof ApiClient.accessToken === "function") {
            return ApiClient.accessToken() || "";
        }
        return ApiClient.accessToken || "";
    }

    function apiRequest(path, options) {
        options = options || {};
        var headers = options.headers || {};
        var token = apiToken();
        if (token) {
            headers["X-Emby-Token"] = token;
        }
        if (options.body && !headers["Content-Type"]) {
            headers["Content-Type"] = "application/json";
        }

        return fetch(apiUrl(path), {
            method: options.method || "GET",
            headers: headers,
            body: options.body,
            credentials: "same-origin"
        }).then(function (response) {
            return response.text().then(function (body) {
                var data = null;
                try {
                    data = body ? JSON.parse(body) : null;
                } catch (ignore) {
                    data = null;
                }
                if (!response.ok) {
                    var message = data && (data.message || data.error) ? (data.message || data.error) : "HTTP " + response.status;
                    throw new Error(message);
                }
                return data;
            });
        });
    }

    function currentUserId() {
        if (window.ApiClient && typeof ApiClient.getCurrentUserId === "function") {
            return ApiClient.getCurrentUserId();
        }
        return "";
    }

    function queryValue(source, name) {
        var match = String(source || "").match(new RegExp("[?&]" + name + "=([^&#]+)", "i"));
        return match ? decodeURIComponent(match[1]) : "";
    }

    function currentItemId() {
        var id = queryValue(window.location.hash, "id") || queryValue(window.location.search, "id");
        if (id) {
            return id;
        }
        var pathMatch = window.location.pathname.match(/\/details\/([^/?#]+)/i);
        return pathMatch ? decodeURIComponent(pathMatch[1]) : "";
    }

    function getItem(itemId) {
        var userId = currentUserId();
        if (window.ApiClient && typeof ApiClient.getItem === "function" && userId) {
            return Promise.resolve(ApiClient.getItem(userId, itemId));
        }
        return apiRequest("Items/" + encodeURIComponent(itemId) + "?Fields=ProviderIds,UserData");
    }

    function providerId(providerIds, name) {
        if (!providerIds) {
            return "";
        }
        var wanted = name.toLowerCase();
        for (var key in providerIds) {
            if (Object.prototype.hasOwnProperty.call(providerIds, key) && key.toLowerCase() === wanted) {
                return String(providerIds[key] || "");
            }
        }
        return "";
    }

    function cinePersonaWebUrl(item) {
        var ids = item && item.ProviderIds ? item.ProviderIds : {};
        var imdbId = providerId(ids, "imdb");
        var tmdbId = providerId(ids, "tmdb");
        if (imdbId) {
            return "https://cinepersona.com/imdb/" + encodeURIComponent(imdbId);
        }
        if (tmdbId) {
            return "https://cinepersona.com/movie/" + encodeURIComponent(tmdbId);
        }
        return "";
    }

    function validRating(value) {
        var rating = Number(value);
        return isFinite(rating) && rating >= 0.5 && rating <= 10 ? rating : 0;
    }

    function ratingStorageKey(itemId) {
        return "cinepersona:rating:" + String(window.location.host || "jellyfin") + ":" + String(itemId || "");
    }

    function readStoredRating(itemId) {
        try {
            return validRating(window.localStorage.getItem(ratingStorageKey(itemId)));
        } catch (ignore) {
            return 0;
        }
    }

    function storeRating(itemId, rating) {
        try {
            window.localStorage.setItem(ratingStorageKey(itemId), String(rating));
        } catch (ignore) {}
    }

    function spoilerStorageKey(itemId) {
        return "cinepersona:spoiler:" + String(window.location.host || "jellyfin") + ":" + String(itemId || "");
    }

    function readStoredSpoiler(itemId) {
        try {
            return window.localStorage.getItem(spoilerStorageKey(itemId)) === "1";
        } catch (ignore) {
            return false;
        }
    }

    function storeSpoiler(itemId, hasSpoiler) {
        try {
            window.localStorage.setItem(spoilerStorageKey(itemId), hasSpoiler ? "1" : "0");
        } catch (ignore) {}
    }

    function activityValue(activity, name) {
        if (!activity) {
            return undefined;
        }
        if (activity[name] !== undefined && activity[name] !== null) {
            return activity[name];
        }
        var lowerName = name.charAt(0).toLowerCase() + name.slice(1);
        return activity[lowerName];
    }

    function itemRating(item) {
        var activityRating = validRating(activityValue(state.activity, "Rating"));
        var nativeRating = item && item.UserData ? validRating(item.UserData.Rating) : 0;
        return activityRating || nativeRating || readStoredRating(item && item.Id);
    }

    function getCinePersonaActivity(item) {
        var ids = item && item.ProviderIds ? item.ProviderIds : {};
        var imdbId = providerId(ids, "imdb");
        var tmdbId = providerId(ids, "tmdb");
        if (!imdbId && !tmdbId) {
            return Promise.resolve(null);
        }

        var query = "CinePersona/Activity?ImdbId=" + encodeURIComponent(imdbId) + "&TmdbId=" + encodeURIComponent(tmdbId);
        return apiRequest(query).then(function (data) {
            return data && (data.Activity || data.activity) ? (data.Activity || data.activity) : null;
        });
    }

    function getCinePersonaSettings() {
        return apiRequest("CinePersona/Settings").then(function (data) {
            data = data || {};
            return {
                Configured: data.Configured !== undefined ? !!data.Configured : !!data.configured,
                Enabled: data.Enabled !== undefined ? data.Enabled !== false : data.enabled !== false
            };
        }).catch(function () {
            return { Configured: false, Enabled: true };
        });
    }

    function formatRating(rating) {
        var value = validRating(rating);
        if (!value) {
            return "评分";
        }
        return (Math.round(value * 10) / 10).toString() + "/10";
    }

    function ensureStyles() {
        if (document.getElementById("cinepersona-web-style")) {
            return;
        }
        var style = document.createElement("style");
        style.id = "cinepersona-web-style";
        style.textContent = "" +
            "/* Jellyfin 内嵌操作栏按钮 */" +
            ".cinepersona-detail-button{display:inline-flex;align-items:center;align-self:center;justify-content:center;min-height:2.55em;margin:0 .55em .5em 0;cursor:pointer;vertical-align:middle;text-decoration:none;border:0;border-radius:.38em;padding:.52em .82em;background:rgba(255,255,255,.16);color:inherit;font-weight:600;font-size:.92em;line-height:1;transition:background .16s ease,color .16s ease;outline:none;box-sizing:border-box}" +
            ".cinepersona-detail-button:hover{background:rgba(255,255,255,.28)}" +
            ".cinepersona-detail-button.is-rated{background:rgba(73,121,182,.32);color:#d8e8ff}" +
            ".cinepersona-detail-button .cinepersona-btn-icon{margin-right:5px;font-size:1em;line-height:1;display:inline-block}" +
            "/* 弹窗核心样式 */" +
            "#cinepersona-review-modal{position:fixed;inset:0;z-index:10001;display:flex;align-items:center;justify-content:center;padding:20px;background:rgba(10,18,30,.58);font-family:system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}" +
            ".cinepersona-review-card{width:min(370px,100%);max-height:calc(100vh - 32px);overflow:auto;border:1px solid rgba(255,255,255,.48);border-radius:14px;padding:18px;background:rgba(250,248,245,.97);color:#253142;box-shadow:0 24px 70px rgba(0,0,0,.34)}" +
            ".cinepersona-review-kicker{margin:0 0 7px;color:#315c9a;font-size:11px;font-weight:750;letter-spacing:.14em;text-transform:uppercase}.cinepersona-review-title{margin:0;font-size:22px;line-height:1.28}.cinepersona-review-copy{margin:8px 0 20px;color:#637083;font-size:13px;line-height:1.5}.cinepersona-review-stars{display:flex;justify-content:center;gap:1px;margin:4px 0 8px}.cinepersona-review-star{border:0;padding:2px;background:transparent;color:#aeb8c5;font-size:27px;line-height:1;cursor:pointer}.cinepersona-review-star.is-selected{color:#315c9a}.cinepersona-review-score{min-height:20px;margin:0 0 17px;text-align:center;color:#315c9a;font-size:13px;font-weight:650}.cinepersona-review-label{display:block;margin:0 0 7px;color:#536173;font-size:12px;font-weight:650}.cinepersona-review-text{display:block;width:100%;min-height:104px;box-sizing:border-box;resize:vertical;border:1px solid #d3dae3;border-radius:10px;padding:11px 12px;background:#fff;color:#253142;font:14px/1.55 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;outline:none}.cinepersona-review-text:focus{border-color:#315c9a;box-shadow:0 0 0 3px rgba(49,92,154,.13)}.cinepersona-review-note{margin:8px 0 0;color:#7b8797;font-size:12px;line-height:1.4}.cinepersona-review-status{min-height:19px;margin:15px 0 0;color:#b42318;font-size:13px;line-height:1.4}.cinepersona-review-actions{display:flex;justify-content:flex-end;gap:10px;margin-top:17px}.cinepersona-review-action{min-width:88px;border:1px solid #315c9a;border-radius:9px;padding:10px 15px;background:transparent;color:#315c9a;font:650 14px system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;cursor:pointer}.cinepersona-review-action.primary{background:#315c9a;color:#fff}.cinepersona-review-action:disabled{opacity:.55;cursor:wait}" +
            ".cinepersona-review-spoiler{display:flex;align-items:center;gap:8px;margin:12px 0 0;color:#536173;font-size:13px;line-height:1.4;cursor:pointer}.cinepersona-review-spoiler input{width:16px;height:16px;margin:0;accent-color:#315c9a}" +
            "/* 暗色模式适配 */" +
            "@media (prefers-color-scheme:dark){.cinepersona-detail-button{background:rgba(255,255,255,.14);color:#edf2f8}.cinepersona-detail-button:hover{background:rgba(255,255,255,.25)}.cinepersona-detail-button.is-rated{background:rgba(73,121,182,.46);color:#e5f0ff}.cinepersona-review-card{background:rgba(30,37,48,.98);color:#edf2f8}.cinepersona-review-copy,.cinepersona-review-note,.cinepersona-review-label,.cinepersona-review-spoiler{color:#aab5c4}.cinepersona-review-text{border-color:#465363;background:#232c38;color:#edf2f8}.cinepersona-review-star{color:#667386}.cinepersona-review-star.is-selected,.cinepersona-review-kicker,.cinepersona-review-score{color:#a9c7f2}.cinepersona-review-action{border-color:#8eb2e4;color:#bcd4f2}.cinepersona-review-action.primary{background:#4779b6;color:#fff}}" +
            ".cinepersona-review-card.theme-dark{background:rgba(30,37,48,.98)!important;color:#edf2f8!important}" +
            ".cinepersona-review-card.theme-dark .cinepersona-review-copy,.cinepersona-review-card.theme-dark .cinepersona-review-note,.cinepersona-review-card.theme-dark .cinepersona-review-label,.cinepersona-review-card.theme-dark .cinepersona-review-spoiler{color:#aab5c4!important}" +
            ".cinepersona-review-card.theme-dark .cinepersona-review-text{border-color:#465363!important;background:#232c38!important;color:#edf2f8!important}" +
            ".cinepersona-review-card.theme-dark .cinepersona-review-star{color:#667386!important}" +
            ".cinepersona-review-card.theme-dark .cinepersona-review-star.is-selected,.cinepersona-review-card.theme-dark .cinepersona-review-kicker,.cinepersona-review-card.theme-dark .cinepersona-review-score{color:#a9c7f2!important}" +
            ".cinepersona-review-card.theme-dark .cinepersona-review-action{border-color:#8eb2e4!important;color:#bcd4f2!important}" +
            ".cinepersona-review-card.theme-dark .cinepersona-review-action.primary{background:#4779b6!important;color:#fff!important}" +
            "@media (max-width:560px){.cinepersona-detail-button{padding:.5em .7em;font-size:.88em}.cinepersona-review-card{padding:16px;border-radius:13px}.cinepersona-review-stars{gap:0}.cinepersona-review-star{font-size:23px}}" +
            ".cinepersona-detail-button{height:2.6em;min-height:2.6em;padding:0 .95em;font-size:1em;font-weight:600;line-height:1}.cinepersona-detail-button .cinepersona-btn-icon,.cinepersona-detail-button .cinepersona-btn-label{display:inline-flex;align-items:center;justify-content:center;height:1em;line-height:1;vertical-align:middle}.cinepersona-detail-button .cinepersona-btn-icon{width:1em;margin-right:.45em}.cinepersona-review-actions{align-items:center;flex-wrap:wrap}.cinepersona-review-action{font:650 14px/1.1 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}.cinepersona-review-action-link{min-width:0;margin-right:auto;border-color:transparent;padding-left:0;padding-right:0;text-decoration:none;white-space:nowrap}.cinepersona-external-link{color:inherit;text-decoration:none}" +
            ".cinepersona-code-box{letter-spacing:4px;font-family:ui-monospace,SFMono-Regular,Menlo,Monaco,Consolas,monospace;font-size:26px;font-weight:700;text-align:center;padding:12px 10px;background:rgba(0,0,0,.06);border:1px dashed #315c9a;border-radius:10px;margin:12px 0 6px;user-select:all;cursor:pointer;transition:background .15s}.cinepersona-code-box:hover{background:rgba(0,0,0,.1)}.cinepersona-review-card.theme-dark .cinepersona-code-box{background:rgba(255,255,255,.08);color:#edf2f8;border-color:#8eb2e4}.cinepersona-review-card.theme-dark .cinepersona-code-box:hover{background:rgba(255,255,255,.14)}.cinepersona-code-hint{font-size:12px;color:#7b8797;text-align:center;margin:0 0 14px}.cinepersona-review-card.theme-dark .cinepersona-code-hint{color:#8f9ba8}.cinepersona-manual-toggle{color:#637083;font-size:12px;cursor:pointer;text-decoration:underline;background:none;border:none;padding:0;margin-top:14px;display:inline-block}.cinepersona-review-card.theme-dark .cinepersona-manual-toggle{color:#aab5c4}.cinepersona-pulse-dot{display:inline-block;width:8px;height:8px;border-radius:50%;background:#315c9a;margin-right:6px;vertical-align:middle;animation:cinepersona-pulse 1.4s infinite ease-in-out}.cinepersona-review-card.theme-dark .cinepersona-pulse-dot{background:#8eb2e4}@keyframes cinepersona-pulse{0%,100%{opacity:.3;transform:scale(.8)}50%{opacity:1;transform:scale(1.2)}}";
        document.head.appendChild(style);
    }

    function isDarkTheme() {
        if (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches) {
            return true;
        }
        var bodyClass = String(document.body ? document.body.className : "") + " " + String(document.documentElement ? document.documentElement.className : "");
        if (/dark|black|wistful|night/i.test(bodyClass)) {
            return true;
        }
        var themeAttr = (document.body && document.body.getAttribute("data-theme")) || (document.documentElement && document.documentElement.getAttribute("data-theme")) || "";
        if (/dark|black|wistful|night/i.test(themeAttr)) {
            return true;
        }
        try {
            var bg = window.getComputedStyle(document.body).backgroundColor;
            var match = bg.match(/rgba?\((\d+),\s*(\d+),\s*(\d+)/);
            if (match) {
                var lum = 0.299 * (+match[1]) + 0.587 * (+match[2]) + 0.114 * (+match[3]);
                if (lum < 130) {
                    return true;
                }
            }
        } catch (e) {}
        return false;
    }

    function makeElement(tag, className, text) {
        var element = document.createElement(tag);
        if (className) {
            element.className = className;
        }
        if (text !== undefined) {
            element.textContent = text;
        }
        return element;
    }

    function removeTriggers() {
        var existingBtn = document.querySelectorAll(".cinepersona-injected-trigger");
        for (var i = 0; i < existingBtn.length; i++) {
            existingBtn[i].remove();
        }
    }

    function isVisibleElement(element) {
        if (!element || !element.getBoundingClientRect) {
            return false;
        }
        var rect = element.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) {
            return false;
        }
        var style = window.getComputedStyle(element);
        return style.display !== "none" && style.visibility !== "hidden" && style.visibility !== "collapse" && style.opacity !== "0";
    }

    function findDetailButtonBar() {
        var selectors = [
            ".mainDetailButtons",
            ".detailButtons",
            ".itemDetailButtons",
            ".detailPagePrimaryContainer .mainDetailButtons",
            ".detailPagePrimaryContainer .detailButtons"
        ];
        var candidates = [];
        for (var i = 0; i < selectors.length; i++) {
            var matches = document.querySelectorAll(selectors[i]);
            for (var k = 0; k < matches.length; k++) {
                if (candidates.indexOf(matches[k]) < 0) {
                    candidates.push(matches[k]);
                }
            }
        }

        var variants = document.querySelectorAll("[class*='mainDetailButtons'],[class*='detailButtons'],[class*='itemDetailButtons']");
        for (var j = 0; j < variants.length; j++) {
            if (candidates.indexOf(variants[j]) < 0) {
                candidates.push(variants[j]);
            }
        }

        // Jellyfin/Emby 保留旧详情页 DOM 时，querySelector 可能先拿到不可见的旧操作栏。
        // 只允许当前可见的操作栏，避免按钮被注入到上一部电影的隐藏节点。
        for (var n = 0; n < candidates.length; n++) {
            if (isVisibleElement(candidates[n]) && candidates[n].querySelector("button,a")) {
                return candidates[n];
            }
        }
        for (var m = 0; m < candidates.length; m++) {
            if (isVisibleElement(candidates[m])) {
                return candidates[m];
            }
        }
        return null;
    }

    function injectCinePersonaLink(item) {
        var url = cinePersonaWebUrl(item);
        if (!url || document.querySelector(".cinepersona-external-link")) {
            return;
        }

        var anchors = document.querySelectorAll("a[href]");
        var databaseAnchors = [];
        for (var i = 0; i < anchors.length; i++) {
            var href = String(anchors[i].href || "").toLowerCase();
            if (href.indexOf("imdb.com/title/") >= 0 || href.indexOf("themoviedb.org/movie/") >= 0 || href.indexOf("thetvdb.com/dereferrer/movie/") >= 0 || href.indexOf("trakt.tv/search/tmdb/") >= 0) {
                databaseAnchors.push(anchors[i]);
            }
        }
        if (!databaseAnchors.length) {
            return;
        }

        var host = databaseAnchors[0].parentElement;
        while (host && host.querySelectorAll("a[href]").length < databaseAnchors.length) {
            host = host.parentElement;
        }
        if (!host) {
            return;
        }

        var link = makeElement("a", "cinepersona-external-link", "CinePersona");
        link.href = url;
        link.target = "_blank";
        link.rel = "noopener noreferrer";
        link.title = "在 CinePersona 查看评分和短评";
        host.appendChild(document.createTextNode(", "));
        host.appendChild(link);
    }

    function removeCinePersonaLinks() {
        var links = document.querySelectorAll(".cinepersona-external-link");
        for (var i = 0; i < links.length; i++) {
            var separator = links[i].previousSibling;
            if (separator && separator.nodeType === 3 && separator.nodeValue === ", ") {
                separator.remove();
            }
            links[i].remove();
        }
    }

    function injectTriggers() {
        ensureStyles();
        var injectedCount = 0;

        // 1. 注入到主操作栏。不同 Jellyfin 主题/版本使用的容器类名并不完全一致。
        var btnBar = findDetailButtonBar();
        if (btnBar && !btnBar.querySelector(".cinepersona-detail-button")) {
            var btn = document.createElement("button");
            btn.type = "button";
            btn.className = "cinepersona-detail-button cinepersona-injected-trigger";
            var rating = itemRating(state.item);
            btn.className += rating ? " is-rated" : "";
            btn.title = state.settings && state.settings.Configured
                ? (rating ? "修改 CinePersona 评分（" + formatRating(rating) + "）" : "在 CinePersona 评分并写短评")
                : "连接 CinePersona";
            btn.setAttribute("aria-label", btn.title);
            btn.innerHTML = '<span class="cinepersona-btn-icon">★</span><span class="cinepersona-btn-label">' + (state.settings && state.settings.Configured ? formatRating(rating) : "连接") + '</span>';
            btn.addEventListener("click", openModal);

            var userRatingBtn = btnBar.querySelector(".btnUserRating");
            if (userRatingBtn && userRatingBtn.parentNode === btnBar) {
                btnBar.insertBefore(btn, userRatingBtn);
            } else {
                btnBar.appendChild(btn);
            }
            injectedCount++;
        }

        injectCinePersonaLink(state.item);

        return injectedCount > 0;
    }

    function setTriggerVisible(visible) {
        if (!visible) {
            removeTriggers();
            return;
        }
        injectTriggers();
    }

    function renderStars(container, scoreLabel) {
        container.innerHTML = "";
        for (var value = 1; value <= 10; value += 1) {
            (function (rating) {
                var star = makeElement("button", "cinepersona-review-star" + (rating <= state.rating ? " is-selected" : ""), rating <= state.rating ? "★" : "☆");
                star.type = "button";
                star.setAttribute("aria-label", rating + " / 10");
                star.addEventListener("click", function () {
                    state.rating = rating;
                    renderStars(container, scoreLabel);
                });
                container.appendChild(star);
            })(value);
        }
        scoreLabel.textContent = state.rating ? state.rating + " / 10" : "请选择评分";
    }

    function closeModal() {
        if (state.pollTimer) {
            clearInterval(state.pollTimer);
            state.pollTimer = null;
        }
        if (!state.modal) {
            return;
        }
        if (state.modal.onKeyDown) {
            document.removeEventListener("keydown", state.modal.onKeyDown);
        }
        state.modal.remove();
        state.modal = null;
    }

    function saveNativeRating(item, rating) {
        var runtimeTicks = Number(item.RunTimeTicks || 0);
        return apiRequest("UserItems/" + encodeURIComponent(item.Id) + "/UserData", {
            method: "POST",
            body: JSON.stringify({
                ItemId: item.Id,
                Rating: rating,
                Played: true,
                PlayedPercentage: 100,
                PlaybackPositionTicks: runtimeTicks
            })
        });
    }

    function sendToCinePersona(item, rating, reviewText, hasSpoiler) {
        var ids = item.ProviderIds || {};
        return apiRequest("CinePersona/Review", {
            method: "POST",
            body: JSON.stringify({
                ItemId: item.Id,
                Name: item.Name || "",
                ProductionYear: Number(item.ProductionYear || 0),
                ImdbId: providerId(ids, "imdb"),
                TmdbId: providerId(ids, "tmdb"),
                Rating: rating,
                ReviewText: reviewText,
                HasSpoiler: !!hasSpoiler
            })
        });
    }

    function openSettingsModal() {
        closeModal();
        var settings = state.settings || { Configured: false, Enabled: true };
        var modal = makeElement("div");
        modal.id = "cinepersona-review-modal";
        modal.setAttribute("role", "dialog");
        modal.setAttribute("aria-modal", "true");

        var card = makeElement("div", "cinepersona-review-card" + (isDarkTheme() ? " theme-dark" : ""));

        function renderConfiguredView() {
            card.innerHTML = "";
            var title = makeElement("h2", "cinepersona-review-title", "CinePersona 连接设置");
            var copy = makeElement("p", "cinepersona-review-copy", "当前 Jellyfin 用户已连接 CinePersona 账号。");

            var enabledLabel = makeElement("label", "cinepersona-review-spoiler");
            var enabledInput = document.createElement("input");
            enabledInput.type = "checkbox";
            enabledInput.checked = settings.Enabled !== false;
            enabledLabel.appendChild(enabledInput);
            enabledLabel.appendChild(document.createTextNode("启用同步"));

            var rebindBtn = makeElement("button", "cinepersona-manual-toggle", "重新绑定账号");
            rebindBtn.type = "button";
            rebindBtn.style.display = "block";
            rebindBtn.style.margin = "12px 0 6px";
            rebindBtn.addEventListener("click", function () {
                renderDeviceCodeView();
            });

            var manualToggle = makeElement("button", "cinepersona-manual-toggle", "修改 API Key（高级）");
            manualToggle.type = "button";
            var manualSection = makeElement("div");
            manualSection.style.display = "none";
            manualSection.style.marginTop = "10px";

            var keyLabel = makeElement("label", "cinepersona-review-label", "API Key");
            var keyInput = makeElement("input", "cinepersona-review-text");
            keyInput.type = "password";
            keyInput.autocomplete = "off";
            keyInput.placeholder = "留空保持当前 Key 不变";
            keyInput.style.minHeight = "38px";
            keyInput.style.height = "38px";
            keyInput.style.padding = "6px 10px";

            var keyLink = makeElement("a", "cinepersona-review-note", "获取 API Key ↗");
            keyLink.href = "https://cinepersona.com/settings";
            keyLink.target = "_blank";
            keyLink.rel = "noopener noreferrer";

            manualSection.appendChild(keyLabel);
            manualSection.appendChild(keyInput);
            manualSection.appendChild(keyLink);

            manualToggle.addEventListener("click", function () {
                var isHidden = manualSection.style.display === "none";
                manualSection.style.display = isHidden ? "block" : "none";
                manualToggle.textContent = isHidden ? "收起 API Key" : "修改 API Key（高级）";
            });

            var status = makeElement("p", "cinepersona-review-status");
            var actions = makeElement("div", "cinepersona-review-actions");
            var cancel = makeElement("button", "cinepersona-review-action", "关闭");
            var submit = makeElement("button", "cinepersona-review-action primary", "保存设置");
            cancel.type = "button";
            submit.type = "button";

            cancel.addEventListener("click", closeModal);
            submit.addEventListener("click", function () {
                submit.disabled = true;
                cancel.disabled = true;
                status.style.color = "";
                status.textContent = "正在保存…";

                var payload = {
                    Enabled: !!enabledInput.checked
                };
                var newKey = keyInput.value.trim();
                if (newKey) {
                    payload.ApiKey = newKey;
                }

                apiRequest("CinePersona/Settings", {
                    method: "POST",
                    body: JSON.stringify(payload)
                }).then(function (result) {
                    state.settings = {
                        Configured: result && (result.Configured !== undefined ? !!result.Configured : !!result.configured),
                        Enabled: result && (result.Enabled !== undefined ? result.Enabled !== false : result.enabled !== false)
                    };
                    status.style.color = "#238653";
                    status.textContent = "已保存。";
                    window.setTimeout(function () {
                        closeModal();
                        refresh();
                    }, 450);
                }).catch(function (error) {
                    submit.disabled = false;
                    cancel.disabled = false;
                    status.textContent = "保存失败：" + (error && error.message ? error.message : "请稍后重试");
                });
            });

            actions.appendChild(cancel);
            actions.appendChild(submit);

            card.appendChild(title);
            card.appendChild(copy);
            card.appendChild(enabledLabel);
            card.appendChild(rebindBtn);
            card.appendChild(manualToggle);
            card.appendChild(manualSection);
            card.appendChild(status);
            card.appendChild(actions);
        }

        function renderDeviceCodeView() {
            if (state.pollTimer) {
                clearInterval(state.pollTimer);
                state.pollTimer = null;
            }

            card.innerHTML = "";
            var title = makeElement("h2", "cinepersona-review-title", "连接 CinePersona");
            var copy = makeElement("p", "cinepersona-review-copy", "在浏览器中确认授权，即可自动完成绑定。");
            var codeContainer = makeElement("div");
            codeContainer.style.textAlign = "center";
            var loadingText = makeElement("p", "cinepersona-review-copy", "正在申请设备授权码…");
            codeContainer.appendChild(loadingText);

            var manualToggle = makeElement("button", "cinepersona-manual-toggle", "手动输入 API Key（高级）");
            manualToggle.type = "button";

            var manualSection = makeElement("div");
            manualSection.style.display = "none";
            manualSection.style.marginTop = "14px";

            var keyLabel = makeElement("label", "cinepersona-review-label", "API Key");
            var keyInput = makeElement("input", "cinepersona-review-text");
            keyInput.type = "password";
            keyInput.autocomplete = "off";
            keyInput.placeholder = "cpk_…";
            keyInput.style.minHeight = "38px";
            keyInput.style.height = "38px";
            keyInput.style.padding = "6px 10px";

            var keyLink = makeElement("a", "cinepersona-review-note", "获取 API Key ↗");
            keyLink.href = "https://cinepersona.com/settings";
            keyLink.target = "_blank";
            keyLink.rel = "noopener noreferrer";

            var manualSaveBtn = makeElement("button", "cinepersona-review-action primary", "保存 Key");
            manualSaveBtn.type = "button";
            manualSaveBtn.style.marginTop = "8px";

            manualSaveBtn.addEventListener("click", function () {
                var val = keyInput.value.trim();
                if (!val) {
                    status.textContent = "请填写 API Key。";
                    return;
                }
                if (state.pollTimer) {
                    clearInterval(state.pollTimer);
                    state.pollTimer = null;
                }
                manualSaveBtn.disabled = true;
                status.style.color = "";
                status.textContent = "正在保存…";
                apiRequest("CinePersona/Settings", {
                    method: "POST",
                    body: JSON.stringify({ ApiKey: val, Enabled: true })
                }).then(function (result) {
                    state.settings = {
                        Configured: result && (result.Configured !== undefined ? !!result.Configured : !!result.configured),
                        Enabled: result && (result.Enabled !== undefined ? result.Enabled !== false : result.enabled !== false)
                    };
                    status.style.color = "#238653";
                    status.textContent = "已连接 CinePersona。";
                    window.setTimeout(function () {
                        closeModal();
                        refresh();
                    }, 450);
                }).catch(function (error) {
                    manualSaveBtn.disabled = false;
                    status.textContent = "保存失败：" + (error && error.message ? error.message : "请稍后重试");
                });
            });

            manualSection.appendChild(keyLabel);
            manualSection.appendChild(keyInput);
            manualSection.appendChild(keyLink);
            manualSection.appendChild(manualSaveBtn);

            manualToggle.addEventListener("click", function () {
                var isHidden = manualSection.style.display === "none";
                manualSection.style.display = isHidden ? "block" : "none";
                manualToggle.textContent = isHidden ? "收起 API Key" : "手动输入 API Key（高级）";
            });

            var status = makeElement("p", "cinepersona-review-status");
            var actions = makeElement("div", "cinepersona-review-actions");
            var cancel = makeElement("button", "cinepersona-review-action", "取消");
            cancel.type = "button";
            cancel.addEventListener("click", closeModal);
            actions.appendChild(cancel);

            card.appendChild(title);
            card.appendChild(copy);
            card.appendChild(codeContainer);
            card.appendChild(manualToggle);
            card.appendChild(manualSection);
            card.appendChild(status);
            card.appendChild(actions);

            apiRequest("CinePersona/DeviceCode", { method: "POST" }).then(function (data) {
                if (!data || !data.userCode) {
                    loadingText.textContent = "获取设备码失败，请重试或使用手动 Key。";
                    return;
                }

                codeContainer.innerHTML = "";
                var userCode = String(data.userCode || "");
                var codeBox = makeElement("div", "cinepersona-code-box", userCode);
                codeBox.title = "点击复制";

                var codeHint = makeElement("p", "cinepersona-code-hint", "点击上方代码快速复制");

                codeBox.addEventListener("click", function () {
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(userCode).then(function () {
                            codeHint.textContent = "已复制到剪贴板！";
                            codeHint.style.color = "#238653";
                            window.setTimeout(function () {
                                codeHint.textContent = "点击上方代码快速复制";
                                codeHint.style.color = "";
                            }, 2000);
                        }).catch(function () {});
                    }
                });

                var verifyUrl = (data.verificationUrl || "https://cinepersona.com/activate") + "?code=" + encodeURIComponent(userCode);
                var authLink = makeElement("a", "cinepersona-review-action primary", "打开网页授权 ↗");
                authLink.href = verifyUrl;
                authLink.target = "_blank";
                authLink.rel = "noopener noreferrer";
                authLink.style.display = "block";
                authLink.style.textAlign = "center";
                authLink.style.textDecoration = "none";
                authLink.style.margin = "8px 0";

                var pollStatus = makeElement("div");
                pollStatus.style.marginTop = "12px";
                pollStatus.style.fontSize = "13px";
                pollStatus.style.textAlign = "center";
                pollStatus.style.color = "#637083";
                pollStatus.innerHTML = '<span class="cinepersona-pulse-dot"></span>等待授权中，完成后将自动连接…';

                codeContainer.appendChild(codeBox);
                codeContainer.appendChild(codeHint);
                codeContainer.appendChild(authLink);
                codeContainer.appendChild(pollStatus);

                var intervalMs = Math.max(Number(data.interval || 3), 2) * 1000;
                state.pollTimer = window.setInterval(function () {
                    apiRequest("CinePersona/DevicePoll", {
                        method: "POST",
                        body: JSON.stringify({ DeviceCode: data.deviceCode })
                    }).then(function (res) {
                        if (!res) return;
                        var s = (res.status || res.Status || "").toLowerCase();
                        if (s === "approved") {
                            if (state.pollTimer) {
                                clearInterval(state.pollTimer);
                                state.pollTimer = null;
                            }
                            state.settings = { Configured: true, Enabled: true };
                            pollStatus.style.color = "#238653";
                            var uName = res.userName || res.UserName || "";
                            pollStatus.textContent = "✓ 授权成功！" + (uName ? "欢迎你，" + uName : "已连接 CinePersona。");
                            window.setTimeout(function () {
                                closeModal();
                                refresh();
                            }, 1200);
                        } else if (s === "expired") {
                            if (state.pollTimer) {
                                clearInterval(state.pollTimer);
                                state.pollTimer = null;
                            }
                            pollStatus.style.color = "#b42318";
                            pollStatus.textContent = "授权码已过期。";
                            var retryBtn = makeElement("button", "cinepersona-review-action", "重新获取");
                            retryBtn.type = "button";
                            retryBtn.style.marginTop = "8px";
                            retryBtn.addEventListener("click", function () {
                                renderDeviceCodeView();
                            });
                            pollStatus.appendChild(document.createElement("br"));
                            pollStatus.appendChild(retryBtn);
                        }
                    }).catch(function () {
                        // ignore temporary polling errors
                    });
                }, intervalMs);
            }).catch(function (err) {
                loadingText.textContent = "获取设备码失败：" + (err && err.message ? err.message : "网络错误");
                var retryBtn = makeElement("button", "cinepersona-review-action", "重试");
                retryBtn.type = "button";
                retryBtn.style.marginTop = "8px";
                retryBtn.addEventListener("click", function () {
                    renderDeviceCodeView();
                });
                codeContainer.appendChild(retryBtn);
            });
        }

        if (settings.Configured) {
            renderConfiguredView();
        } else {
            renderDeviceCodeView();
        }

        modal.appendChild(card);
        modal.addEventListener("click", function (event) {
            if (event.target === modal) {
                closeModal();
            }
        });
        modal.onKeyDown = function (event) {
            if (event.key === "Escape") {
                closeModal();
            }
        };
        document.addEventListener("keydown", modal.onKeyDown);
        state.modal = modal;
        document.body.appendChild(modal);
    }

    function openModal() {
        if (!state.item || state.item.Type !== "Movie") {
            return;
        }
        if (!state.settings || !state.settings.Configured) {
            openSettingsModal();
            return;
        }
        closeModal();
        state.rating = itemRating(state.item);
        var activityReview = activityValue(state.activity, "ReviewText");
        var activitySpoiler = activityValue(state.activity, "HasSpoiler");
        state.hasSpoiler = activitySpoiler !== undefined
            ? !!activitySpoiler
            : readStoredSpoiler(state.item && state.item.Id);

        var modal = makeElement("div");
        modal.id = "cinepersona-review-modal";
        modal.setAttribute("role", "dialog");
        modal.setAttribute("aria-modal", "true");
        var card = makeElement("div", "cinepersona-review-card" + (isDarkTheme() ? " theme-dark" : ""));
        var title = makeElement("h2", "cinepersona-review-title", state.item.Name || "评价这部电影");
        var stars = makeElement("div", "cinepersona-review-stars");
        var score = makeElement("p", "cinepersona-review-score");
        var label = makeElement("label", "cinepersona-review-label", "短评（可选）");
        var textarea = makeElement("textarea", "cinepersona-review-text");
        textarea.id = "cinepersona-review-text";
        textarea.placeholder = "写下这部电影给你的感受…";
        textarea.maxLength = 4000;
        textarea.value = typeof activityReview === "string" ? activityReview : "";
        var spoilerLabel = makeElement("label", "cinepersona-review-spoiler");
        var spoilerInput = document.createElement("input");
        spoilerInput.type = "checkbox";
        spoilerInput.checked = state.hasSpoiler;
        spoilerLabel.appendChild(spoilerInput);
        spoilerLabel.appendChild(document.createTextNode("这条短评包含剧透"));
        var status = makeElement("p", "cinepersona-review-status");
        var actions = makeElement("div", "cinepersona-review-actions");
        var webLink = makeElement("a", "cinepersona-review-action cinepersona-review-action-link", "看完整评价");
        var settingsLink = makeElement("button", "cinepersona-review-action cinepersona-review-action-link", "同步设置");
        var cancel = makeElement("button", "cinepersona-review-action", "取消");
        var submit = makeElement("button", "cinepersona-review-action primary", "保存");
        cancel.type = "button";
        submit.type = "button";
        settingsLink.type = "button";
        settingsLink.addEventListener("click", openSettingsModal);
        var webUrl = cinePersonaWebUrl(state.item);
        if (webUrl) {
            webLink.href = webUrl;
            webLink.target = "_blank";
            webLink.rel = "noopener noreferrer";
            webLink.title = "打开 CinePersona 电影页";
        } else {
            webLink.style.display = "none";
        }
        label.htmlFor = textarea.id;
        renderStars(stars, score);
        cancel.addEventListener("click", closeModal);
        submit.addEventListener("click", function () {
            if (!state.rating) {
                status.textContent = "请先选择评分。";
                return;
            }
            var reviewText = textarea.value.trim();
            state.hasSpoiler = !!spoilerInput.checked;
            submit.disabled = true;
            cancel.disabled = true;
            status.style.color = "";
            status.textContent = "正在保存…";
            Promise.all([
                saveNativeRating(state.item, state.rating),
                sendToCinePersona(state.item, state.rating, reviewText, state.hasSpoiler)
            ]).then(function () {
                if (state.item.UserData) {
                    state.item.UserData.Rating = state.rating;
                    state.item.UserData.Played = true;
                } else {
                    state.item.UserData = { Rating: state.rating, Played: true };
                }
                storeRating(state.item.Id, state.rating);
                storeSpoiler(state.item.Id, state.hasSpoiler && reviewText.length > 0);
                removeTriggers();
                injectTriggers();
                status.style.color = "#238653";
                status.textContent = "已保存到 Jellyfin 和 CinePersona。";
                window.setTimeout(closeModal, 650);
            }).catch(function (error) {
                submit.disabled = false;
                cancel.disabled = false;
                status.textContent = "保存失败：" + (error && error.message ? error.message : "请稍后重试");
            });
        });
        actions.appendChild(webLink);
        actions.appendChild(settingsLink);
        actions.appendChild(cancel);
        actions.appendChild(submit);
        card.appendChild(title);
        card.appendChild(stars);
        card.appendChild(score);
        card.appendChild(label);
        card.appendChild(textarea);
        card.appendChild(spoilerLabel);
        card.appendChild(status);
        card.appendChild(actions);
        modal.appendChild(card);
        modal.addEventListener("click", function (event) {
            if (event.target === modal) {
                closeModal();
            }
        });
        state.modal = modal;
        state.modal.onKeyDown = function (event) {
            if (event.key === "Escape") {
                closeModal();
            }
        };
        document.addEventListener("keydown", state.modal.onKeyDown);
        document.body.appendChild(modal);
        textarea.focus();
    }

    function refresh() {
        var itemId = currentItemId();
        if (!itemId) {
            removeCinePersonaLinks();
            state.itemId = null;
            state.item = null;
            state.activity = null;
            setTriggerVisible(false);
            closeModal();
            return;
        }
        if (itemId === state.itemId && state.item) {
            setTriggerVisible(state.item.Type === "Movie");
            return;
        }
        state.itemId = itemId;
        state.item = null;
        state.activity = null;
        removeCinePersonaLinks();
        setTriggerVisible(false);
        getItem(itemId).then(function (item) {
            if (state.itemId !== itemId) {
                return;
            }
            state.item = item;
            state.activity = null;
            setTriggerVisible(item && item.Type === "Movie");
            if (!item || item.Type !== "Movie") {
                return;
            }
            getCinePersonaActivity(item).then(function (activity) {
                if (state.itemId !== itemId) {
                    return;
                }
                state.activity = activity;
                removeTriggers();
                setTriggerVisible(true);
            }).catch(function () {
                // 评分回显失败时仍保留平台本地评分和本地缓存的操作入口。
            });
        }).catch(function () {
            if (state.itemId === itemId) {
                state.activity = null;
                setTriggerVisible(false);
            }
        });
    }

    function refreshSettings() {
        getCinePersonaSettings().then(function (settings) {
            state.settings = settings;
            removeTriggers();
            if (state.item && state.item.Type === "Movie") {
                setTriggerVisible(true);
            }
        });
    }

    function scheduleRefresh() {
        if (state.refreshScheduled) {
            return;
        }
        state.refreshScheduled = true;
        window.setTimeout(function () {
            state.refreshScheduled = false;
            refresh();
        }, 120);
    }

    function installNavigationWatchers() {
        if (!window.__cinePersonaNavigationWatchers) {
            ["pushState", "replaceState"].forEach(function (methodName) {
                var original = window.history[methodName];
                if (typeof original !== "function") {
                    return;
                }
                window.history[methodName] = function () {
                    var result = original.apply(this, arguments);
                    scheduleRefresh();
                    return result;
                };
            });
            window.__cinePersonaNavigationWatchers = true;
        }

        window.addEventListener("hashchange", scheduleRefresh);
        window.addEventListener("popstate", scheduleRefresh);
        document.addEventListener("click", function () {
            scheduleRefresh();
            window.setTimeout(scheduleRefresh, 700);
        }, true);
        if (window.MutationObserver && document.body) {
            var observer = new MutationObserver(function (mutations) {
                for (var i = 0; i < mutations.length; i++) {
                    if (mutations[i].type === "childList") {
                        scheduleRefresh();
                        return;
                    }
                }
            });
            observer.observe(document.body, { childList: true, subtree: true });
        }
    }

    ensureStyles();
    installNavigationWatchers();
    window.setInterval(refresh, 1000);
    refreshSettings();
    refresh();
})();
