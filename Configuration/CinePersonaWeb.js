(function () {
    "use strict";

    // 强杀旧版本遗留的右下角悬浮球
    try {
        var legacyTrigger = document.getElementById("cinepersona-review-trigger");
        if (legacyTrigger) {
            legacyTrigger.remove();
        }
    } catch (e) {}

    if (window.__cinePersonaWebVersion === "0.2.5") {
        return;
    }
    window.__cinePersonaWebVersion = "0.2.5";
    window.__cinePersonaWebLoaded = true;

    var state = {
        itemId: null,
        item: null,
        modal: null,
        rating: 0,
        hasSpoiler: false,
        activity: null
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

        return apiRequest("Users/" + encodeURIComponent(userId) + "/Items/" + encodeURIComponent(itemId) + "?Fields=ProviderIds,UserData");
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
        return "cinepersona:rating:" + String(window.location.host || "emby") + ":" + String(itemId || "");
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
        return "cinepersona:spoiler:" + String(window.location.host || "emby") + ":" + String(itemId || "");
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
            "/* Emby 内嵌操作栏按钮 */" +
            ".cinepersona-detail-button{display:inline-flex;align-items:center;align-self:center;justify-content:center;box-sizing:border-box;height:2.45em;min-height:0;margin:0 .5em .5em 0;padding:.58em 1em;cursor:pointer;vertical-align:middle;text-decoration:none;border:0;border-radius:.4em;background:rgba(255,255,255,.16);color:inherit;font:inherit;font-weight:500;line-height:normal;transition:background .16s ease,color .16s ease;outline:none}" +
            ".cinepersona-detail-button:hover{background:rgba(255,255,255,.28)}" +
            ".cinepersona-detail-button.is-rated{background:rgba(73,121,182,.32);color:#d8e8ff}" +
            ".cinepersona-detail-button .cinepersona-btn-icon{display:inline-flex;align-items:center;justify-content:center;width:1em;height:1em;margin-right:.45em;font:inherit;line-height:1}" +
            "/* 弹窗核心样式 */" +
            "#cinepersona-review-modal{position:fixed;inset:0;z-index:10001;display:flex;align-items:center;justify-content:center;padding:20px;background:rgba(10,18,30,.58);font-family:system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}" +
            ".cinepersona-review-card{width:min(370px,100%);max-height:calc(100vh - 32px);overflow:auto;border:1px solid rgba(255,255,255,.48);border-radius:14px;padding:18px;background:rgba(250,248,245,.97);color:#253142;box-shadow:0 24px 70px rgba(0,0,0,.34)}" +
            ".cinepersona-review-kicker{margin:0 0 7px;color:#315c9a;font-size:11px;font-weight:750;letter-spacing:.14em;text-transform:uppercase}.cinepersona-review-title{margin:0;font-size:22px;line-height:1.28}.cinepersona-review-copy{margin:8px 0 20px;color:#637083;font-size:13px;line-height:1.5}.cinepersona-review-stars{display:flex;justify-content:center;gap:1px;margin:4px 0 8px}.cinepersona-review-star{border:0;padding:2px;background:transparent;color:#aeb8c5;font-size:27px;line-height:1;cursor:pointer}.cinepersona-review-star.is-selected{color:#315c9a}.cinepersona-review-score{min-height:20px;margin:0 0 17px;text-align:center;color:#315c9a;font-size:13px;font-weight:650}.cinepersona-review-label{display:block;margin:0 0 7px;color:#536173;font-size:12px;font-weight:650}.cinepersona-review-text{display:block;width:100%;min-height:104px;box-sizing:border-box;resize:vertical;border:1px solid #d3dae3;border-radius:10px;padding:11px 12px;background:#fff;color:#253142;font:14px/1.55 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;outline:none}.cinepersona-review-text:focus{border-color:#315c9a;box-shadow:0 0 0 3px rgba(49,92,154,.13)}.cinepersona-review-note{margin:8px 0 0;color:#7b8797;font-size:12px;line-height:1.4}.cinepersona-review-status{min-height:19px;margin:15px 0 0;color:#b42318;font-size:13px;line-height:1.4}.cinepersona-review-actions{display:flex;justify-content:flex-end;gap:10px;margin-top:17px}.cinepersona-review-action{min-width:88px;border:1px solid #315c9a;border-radius:9px;padding:10px 15px;background:transparent;color:#315c9a;font:650 14px system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;cursor:pointer}.cinepersona-review-action.primary{background:#315c9a;color:#fff}.cinepersona-review-action:disabled{opacity:.55;cursor:wait}" +
            ".cinepersona-review-spoiler{display:flex;align-items:center;gap:8px;margin:12px 0 0;color:#536173;font-size:13px;line-height:1.4;cursor:pointer}.cinepersona-review-spoiler input{width:16px;height:16px;margin:0;accent-color:#315c9a}" +
            "/* 暗色模式适配 */" +
            "@media (prefers-color-scheme:dark){.cinepersona-detail-button{background:rgba(255,255,255,.14);color:#edf2f8}.cinepersona-detail-button:hover{background:rgba(255,255,255,.25)}.cinepersona-detail-button.is-rated{background:rgba(73,121,182,.46);color:#e5f0ff}.cinepersona-review-card{background:rgba(30,37,48,.98);color:#edf2f8}.cinepersona-review-copy,.cinepersona-review-note,.cinepersona-review-label,.cinepersona-review-spoiler{color:#aab5c4}.cinepersona-review-text{border-color:#465363;background:#232c38;color:#edf2f8}.cinepersona-review-star{color:#667386}.cinepersona-review-star.is-selected,.cinepersona-review-kicker,.cinepersona-review-score{color:#a9c7f2}.cinepersona-review-action{border-color:#8eb2e4;color:#bcd4f2}.cinepersona-review-action.primary{background:#4779b6;color:#fff}}" +
            "@media (max-width:560px){.cinepersona-detail-button{padding:.5em .8em}.cinepersona-review-card{padding:16px;border-radius:13px}.cinepersona-review-stars{gap:0}.cinepersona-review-star{font-size:23px}}" +
            ".cinepersona-detail-button{height:2.6em;min-height:2.6em;padding:0 .95em;font-size:1em;font-weight:600;line-height:1}.cinepersona-detail-button .cinepersona-btn-icon,.cinepersona-detail-button .cinepersona-btn-label{display:inline-flex;align-items:center;justify-content:center;height:1em;line-height:1;vertical-align:middle}.cinepersona-detail-button .cinepersona-btn-icon{width:1em;margin-right:.45em}.cinepersona-review-actions{align-items:center;flex-wrap:wrap}.cinepersona-review-action{font:650 14px/1.1 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}.cinepersona-review-action-link{min-width:0;margin-right:auto;border-color:transparent;padding-left:0;padding-right:0;text-decoration:none;white-space:nowrap}.cinepersona-external-link{color:inherit;text-decoration:none}";
        document.head.appendChild(style);
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

    function findDetailButtonBar() {
        var selectors = [
            ".mainDetailButtons",
            ".detailButtons",
            ".itemDetailButtons",
            ".detailPagePrimaryContainer .mainDetailButtons",
            ".detailPagePrimaryContainer .detailButtons"
        ];
        for (var i = 0; i < selectors.length; i++) {
            var direct = document.querySelector(selectors[i]);
            if (direct) {
                return direct;
            }
        }

        var variants = document.querySelectorAll("[class*='mainDetailButtons'],[class*='detailButtons'],[class*='itemDetailButtons']");
        for (var j = 0; j < variants.length; j++) {
            if (variants[j].querySelector("button,a")) {
                return variants[j];
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

        // 1. 注入到主操作栏。不同 Emby 主题/版本使用的容器类名并不完全一致。
        var btnBar = findDetailButtonBar();
        if (btnBar && !btnBar.querySelector(".cinepersona-detail-button")) {
            var btn = document.createElement("button");
            btn.type = "button";
            btn.className = "cinepersona-detail-button cinepersona-injected-trigger";
            var rating = itemRating(state.item);
            btn.className += rating ? " is-rated" : "";
            btn.title = rating ? "修改 CinePersona 评分（" + formatRating(rating) + "）" : "在 CinePersona 评分并写短评";
            btn.setAttribute("aria-label", btn.title);
            btn.innerHTML = '<span class="cinepersona-btn-icon">★</span><span class="cinepersona-btn-label">' + formatRating(rating) + '</span>';
            btn.addEventListener("click", openModal);

            // 插入在合适的位置（例如如果是评分按钮之前或直接加在主按钮最后）
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
        if (!state.modal) {
            return;
        }
        document.removeEventListener("keydown", state.modal.onKeyDown);
        state.modal.remove();
        state.modal = null;
    }

    function saveNativeRating(item, rating) {
        var userId = currentUserId();
        var runtimeTicks = Number(item.RunTimeTicks || 0);
        return apiRequest("Users/" + encodeURIComponent(userId) + "/Items/" + encodeURIComponent(item.Id) + "/UserData", {
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

    function openModal() {
        if (!state.item || state.item.Type !== "Movie") {
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

        var card = makeElement("div", "cinepersona-review-card");
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
        var cancel = makeElement("button", "cinepersona-review-action", "取消");
        var submit = makeElement("button", "cinepersona-review-action primary", "保存");
        cancel.type = "button";
        submit.type = "button";
        var webUrl = cinePersonaWebUrl(state.item);
        if (webUrl) {
            webLink.href = webUrl;
            webLink.target = "_blank";
            webLink.rel = "noopener noreferrer";
            webLink.title = "打开 CinePersona 电影页";
        } else {
            webLink.style.display = "none";
        }
        label.htmlFor = "cinepersona-review-text";

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
                status.textContent = "已保存到 Emby 和 CinePersona。";
                window.setTimeout(closeModal, 650);
            }).catch(function (error) {
                submit.disabled = false;
                cancel.disabled = false;
                status.textContent = "保存失败：" + (error && error.message ? error.message : "请稍后重试");
            });
        });

        actions.appendChild(webLink);
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

    ensureStyles();
    window.addEventListener("hashchange", refresh);
    window.addEventListener("popstate", refresh);
    window.setInterval(refresh, 1000);
    refresh();
})();
