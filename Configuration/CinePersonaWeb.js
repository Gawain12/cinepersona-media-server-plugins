(function () {
    "use strict";

    if (window.__cinePersonaWebLoaded) {
        return;
    }
    window.__cinePersonaWebLoaded = true;

    var state = {
        itemId: null,
        item: null,
        modal: null,
        rating: 0
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

    function ensureStyles() {
        if (document.getElementById("cinepersona-web-style")) {
            return;
        }
        var style = document.createElement("style");
        style.id = "cinepersona-web-style";
        style.textContent = "" +
            "#cinepersona-review-trigger{position:fixed;right:22px;bottom:24px;z-index:10000;border:1px solid rgba(49,92,154,.36);border-radius:999px;padding:9px 16px;background:#f8fbff;color:#315c9a;font:600 14px/1.2 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;box-shadow:0 8px 24px rgba(19,38,63,.18);cursor:pointer;transition:transform .16s ease,box-shadow .16s ease}#cinepersona-review-trigger:hover{transform:translateY(-1px);box-shadow:0 10px 28px rgba(19,38,63,.24)}" +
            "#cinepersona-review-modal{position:fixed;inset:0;z-index:10001;display:flex;align-items:center;justify-content:center;padding:20px;background:rgba(10,18,30,.58);font-family:system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif}" +
            ".cinepersona-review-card{width:min(430px,100%);max-height:calc(100vh - 40px);overflow:auto;border:1px solid rgba(255,255,255,.48);border-radius:18px;padding:25px;background:rgba(250,248,245,.97);color:#253142;box-shadow:0 24px 70px rgba(0,0,0,.34)}" +
            ".cinepersona-review-kicker{margin:0 0 7px;color:#315c9a;font-size:11px;font-weight:750;letter-spacing:.14em;text-transform:uppercase}.cinepersona-review-title{margin:0;font-size:22px;line-height:1.28}.cinepersona-review-copy{margin:8px 0 20px;color:#637083;font-size:13px;line-height:1.5}.cinepersona-review-stars{display:flex;justify-content:center;gap:1px;margin:4px 0 8px}.cinepersona-review-star{border:0;padding:2px;background:transparent;color:#aeb8c5;font-size:27px;line-height:1;cursor:pointer}.cinepersona-review-star.is-selected{color:#315c9a}.cinepersona-review-score{min-height:20px;margin:0 0 17px;text-align:center;color:#315c9a;font-size:13px;font-weight:650}.cinepersona-review-label{display:block;margin:0 0 7px;color:#536173;font-size:12px;font-weight:650}.cinepersona-review-text{display:block;width:100%;min-height:104px;box-sizing:border-box;resize:vertical;border:1px solid #d3dae3;border-radius:10px;padding:11px 12px;background:#fff;color:#253142;font:14px/1.55 system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;outline:none}.cinepersona-review-text:focus{border-color:#315c9a;box-shadow:0 0 0 3px rgba(49,92,154,.13)}.cinepersona-review-note{margin:8px 0 0;color:#7b8797;font-size:12px;line-height:1.4}.cinepersona-review-status{min-height:19px;margin:15px 0 0;color:#b42318;font-size:13px;line-height:1.4}.cinepersona-review-actions{display:flex;justify-content:flex-end;gap:10px;margin-top:17px}.cinepersona-review-action{min-width:88px;border:1px solid #315c9a;border-radius:9px;padding:10px 15px;background:transparent;color:#315c9a;font:650 14px system-ui,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;cursor:pointer}.cinepersona-review-action.primary{background:#315c9a;color:#fff}.cinepersona-review-action:disabled{opacity:.55;cursor:wait}" +
            "@media (prefers-color-scheme:dark){#cinepersona-review-trigger{background:#18263a;color:#a9c7f2;border-color:#557eaf}.cinepersona-review-card{background:rgba(30,37,48,.98);color:#edf2f8}.cinepersona-review-copy,.cinepersona-review-note,.cinepersona-review-label{color:#aab5c4}.cinepersona-review-text{border-color:#465363;background:#232c38;color:#edf2f8}.cinepersona-review-star{color:#667386}.cinepersona-review-star.is-selected,.cinepersona-review-kicker,.cinepersona-review-score{color:#a9c7f2}.cinepersona-review-action{border-color:#8eb2e4;color:#bcd4f2}.cinepersona-review-action.primary{background:#4779b6;color:#fff}}" +
            "@media (max-width:560px){#cinepersona-review-trigger{right:14px;bottom:14px}.cinepersona-review-card{padding:21px;border-radius:15px}.cinepersona-review-stars{gap:0}.cinepersona-review-star{font-size:24px}}";
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

    function ensureTrigger() {
        var trigger = document.getElementById("cinepersona-review-trigger");
        if (trigger) {
            return trigger;
        }
        trigger = makeElement("button", "", "评价");
        trigger.id = "cinepersona-review-trigger";
        trigger.type = "button";
        trigger.title = "给这部电影评分并写短评";
        trigger.addEventListener("click", openModal);
        document.body.appendChild(trigger);
        return trigger;
    }

    function setTriggerVisible(visible) {
        var trigger = document.getElementById("cinepersona-review-trigger");
        if (!visible) {
            if (trigger) {
                trigger.remove();
            }
            return;
        }
        ensureStyles();
        ensureTrigger();
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

    function sendToCinePersona(item, rating, reviewText) {
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
                ReviewText: reviewText
            })
        });
    }

    function openModal() {
        if (!state.item || state.item.Type !== "Movie") {
            return;
        }
        closeModal();
        state.rating = Number(state.item.UserData && state.item.UserData.Rating) || 0;

        var modal = makeElement("div");
        modal.id = "cinepersona-review-modal";
        modal.setAttribute("role", "dialog");
        modal.setAttribute("aria-modal", "true");

        var card = makeElement("div", "cinepersona-review-card");
        var kicker = makeElement("p", "cinepersona-review-kicker", "CinePersona");
        var title = makeElement("h2", "cinepersona-review-title", state.item.Name || "评价这部电影");
        var copy = makeElement("p", "cinepersona-review-copy", "评分会保存到 Emby 和 CinePersona；短评只同步到 CinePersona。");
        var stars = makeElement("div", "cinepersona-review-stars");
        var score = makeElement("p", "cinepersona-review-score");
        var label = makeElement("label", "cinepersona-review-label", "短评（可选）");
        var textarea = makeElement("textarea", "cinepersona-review-text");
        textarea.id = "cinepersona-review-text";
        textarea.placeholder = "写下这部电影给你的感受…";
        textarea.maxLength = 4000;
        var note = makeElement("p", "cinepersona-review-note", "电影会按已看过处理，评分范围 1–10。");
        var status = makeElement("p", "cinepersona-review-status");
        var actions = makeElement("div", "cinepersona-review-actions");
        var cancel = makeElement("button", "cinepersona-review-action", "取消");
        var submit = makeElement("button", "cinepersona-review-action primary", "提交评价");
        cancel.type = "button";
        submit.type = "button";
        label.htmlFor = "cinepersona-review-text";

        renderStars(stars, score);
        cancel.addEventListener("click", closeModal);
        submit.addEventListener("click", function () {
            if (!state.rating) {
                status.textContent = "请先选择评分。";
                return;
            }
            var reviewText = textarea.value.trim();
            submit.disabled = true;
            cancel.disabled = true;
            status.style.color = "";
            status.textContent = "正在保存…";
            Promise.all([
                saveNativeRating(state.item, state.rating),
                sendToCinePersona(state.item, state.rating, reviewText)
            ]).then(function () {
                if (state.item.UserData) {
                    state.item.UserData.Rating = state.rating;
                    state.item.UserData.Played = true;
                }
                status.style.color = "#238653";
                status.textContent = "已保存到 Emby 和 CinePersona。";
                window.setTimeout(closeModal, 650);
            }).catch(function (error) {
                submit.disabled = false;
                cancel.disabled = false;
                status.textContent = "保存失败：" + (error && error.message ? error.message : "请稍后重试");
            });
        });

        actions.appendChild(cancel);
        actions.appendChild(submit);
        card.appendChild(kicker);
        card.appendChild(title);
        card.appendChild(copy);
        card.appendChild(stars);
        card.appendChild(score);
        card.appendChild(label);
        card.appendChild(textarea);
        card.appendChild(note);
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
            state.itemId = null;
            state.item = null;
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
        setTriggerVisible(false);
        getItem(itemId).then(function (item) {
            if (state.itemId !== itemId) {
                return;
            }
            state.item = item;
            setTriggerVisible(item && item.Type === "Movie");
        }).catch(function () {
            if (state.itemId === itemId) {
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
