define([], function () {
    return function (page) {
        var pluginUniqueId = "f62e8471-469b-43d8-b57f-f4a4d7d10001";
        var form;
        var mainApiKeyInput;
        var reverseSyncInput;
        var profilesContainer;
        var addProfileButton;
        var saveButton;
        var status;
        var initialized = false;
        var mediaUsers = [];

        function initialize() {
            if (initialized) {
                return;
            }

            form = page.querySelector("#cinepersonaConfigurationForm");
            mainApiKeyInput = page.querySelector("#txtApiKey");
            reverseSyncInput = page.querySelector("#chkReverseSync");
            profilesContainer = page.querySelector("#cinepersonaProfiles");
            addProfileButton = page.querySelector("#btnAddProfile");
            saveButton = page.querySelector("#btnSave");
            status = page.querySelector("#cinepersonaSaveStatus");

            if (!form || !mainApiKeyInput || !profilesContainer) {
                return;
            }

            initialized = true;
            form.addEventListener("submit", submit);
            if (addProfileButton) {
                addProfileButton.addEventListener("click", function () {
                    addProfileRow({ UserId: "", ApiKey: "", Enabled: true });
                });
            }
            load();
        }

        function setStatus(message, state) {
            if (!status) {
                return;
            }

            status.textContent = message || "";
            status.setAttribute("data-state", state || "");
        }

        function getUsers() {
            if (window.ApiClient && typeof ApiClient.getUsers === "function") {
                return Promise.resolve(ApiClient.getUsers()).then(function (users) {
                    return Array.isArray(users) ? users : (users && Array.isArray(users.Items) ? users.Items : []);
                });
            }
            var url = window.ApiClient && typeof ApiClient.getUrl === "function" ? ApiClient.getUrl("Users") : "/Users";
            var token = window.ApiClient && typeof ApiClient.accessToken === "function" ? ApiClient.accessToken() : "";
            return fetch(url, { headers: token ? { "X-Emby-Token": token } : {}, credentials: "same-origin" })
                .then(function (response) { return response.ok ? response.json() : []; })
                .then(function (users) { return Array.isArray(users) ? users : (users && Array.isArray(users.Items) ? users.Items : []); })
                .catch(function () { return []; });
        }

        function userId(user) {
            return user && (user.Id || user.id) ? String(user.Id || user.id) : "";
        }

        function userName(user) {
            return user && (user.Name || user.name) ? String(user.Name || user.name) : "未命名用户";
        }

        function currentUserId() {
            if (window.ApiClient && typeof ApiClient.getCurrentUserId === "function") {
                return ApiClient.getCurrentUserId() || "";
            }
            return "";
        }

        function normalizedProfiles(config) {
            var profiles = Array.isArray(config.UserProfiles) ? config.UserProfiles.slice() : [];
            if (config.ApiKey && config.SyncUserId && !profiles.some(function (profile) {
                return profile && String(profile.UserId || profile.userId || "").toLowerCase() === String(config.SyncUserId).toLowerCase();
            })) {
                profiles.push({
                    UserId: config.SyncUserId,
                    ApiKey: config.ApiKey,
                    Enabled: true,
                    InitialSyncCompleted: !!config.InitialSyncCompleted,
                    LastSyncAt: config.LastSyncAt || null
                });
            }
            return profiles;
        }

        function findProfile(profiles, userIdValue) {
            var normalizedId = String(userIdValue || "").toLowerCase();
            return (profiles || []).find(function (profile) {
                return profile && String(profile.UserId || profile.userId || "").toLowerCase() === normalizedId;
            });
        }

        function createElement(tagName, className, text) {
            var element = document.createElement(tagName);
            if (className) {
                element.className = className;
            }
            if (text) {
                element.textContent = text;
            }
            return element;
        }

        function addUserOption(select, selectedId) {
            var found = false;
            mediaUsers.forEach(function (user) {
                var id = userId(user);
                if (!id) {
                    return;
                }
                var option = createElement("option", "", userName(user));
                option.value = id;
                if (id.toLowerCase() === String(selectedId || "").toLowerCase()) {
                    option.selected = true;
                    found = true;
                }
                select.appendChild(option);
            });
            if (selectedId && !found) {
                var legacyOption = createElement("option", "", "已保存用户（" + selectedId + "）");
                legacyOption.value = selectedId;
                legacyOption.selected = true;
                select.appendChild(legacyOption);
            }
            if (!selectedId) {
                select.insertBefore(createElement("option", "", "选择媒体服务器用户"), select.firstChild);
                select.firstChild.value = "";
                select.firstChild.disabled = true;
                select.firstChild.selected = true;
            }
        }

        function addProfileRow(profile) {
            if (!profilesContainer) {
                return;
            }
            var row = createElement("div", "cinepersonaProfileRow");
            var select = createElement("select", "cinepersonaProfileUser");
            var key = createElement("input", "cinepersonaProfileKey");
            var enabledLabel = createElement("label", "cinepersonaProfileEnabled");
            var enabled = document.createElement("input");
            var remove = createElement("button", "raised button-submit cinepersonaRemoveProfile", "移除");

            select.required = true;
            addUserOption(select, profile.UserId || profile.userId || "");
            key.type = "password";
            key.autocomplete = "new-password";
            key.placeholder = profile.ApiKey ? "已配置，留空保持不变" : "cpk_…";
            key.value = "";
            key.setAttribute("data-existing-key", profile.ApiKey || profile.apiKey || "");
            enabled.type = "checkbox";
            enabled.checked = profile.Enabled !== false && profile.enabled !== false;
            enabledLabel.appendChild(enabled);
            enabledLabel.appendChild(document.createTextNode("启用"));
            remove.type = "button";
            remove.addEventListener("click", function () {
                row.remove();
                renderEmptyState();
            });

            row.appendChild(select);
            row.appendChild(key);
            row.appendChild(enabledLabel);
            row.appendChild(remove);
            profilesContainer.appendChild(row);
            renderEmptyState();
        }

        function renderEmptyState() {
            if (!profilesContainer) {
                return;
            }
            var empty = profilesContainer.querySelector(".cinepersonaProfileEmpty");
            var hasRows = profilesContainer.querySelector(".cinepersonaProfileRow");
            if (!hasRows && !empty) {
                profilesContainer.appendChild(createElement("div", "cinepersonaProfileEmpty", "尚未配置用户。点击“添加用户”开始。"));
            } else if (hasRows && empty) {
                empty.remove();
            }
        }

        function renderProfiles(profiles) {
            if (!profilesContainer) {
                return;
            }
            profilesContainer.innerHTML = "";
            (profiles || []).forEach(addProfileRow);
            renderEmptyState();
        }

        function collectProfiles() {
            var rows = profilesContainer ? profilesContainer.querySelectorAll(".cinepersonaProfileRow") : [];
            var profiles = [];
            var userIds = {};
            for (var i = 0; i < rows.length; i++) {
                var user = rows[i].querySelector(".cinepersonaProfileUser");
                var key = rows[i].querySelector(".cinepersonaProfileKey");
                var enabled = rows[i].querySelector("input[type=checkbox]");
                var id = user ? String(user.value || "").trim() : "";
                var apiKey = key ? String(key.value || "").trim() : "";
                var existingKey = key ? key.getAttribute("data-existing-key") || "" : "";
                if (!id) {
                    throw new Error("请选择每条配置对应的媒体服务器用户。");
                }
                if (userIds[id.toLowerCase()]) {
                    throw new Error("同一个用户不能重复配置。");
                }
                if (!apiKey) {
                    apiKey = existingKey;
                }
                if (!apiKey) {
                    throw new Error("请为“" + (user.options[user.selectedIndex] ? user.options[user.selectedIndex].text : id) + "”填写 API Key。");
                }
                userIds[id.toLowerCase()] = true;
                profiles.push({ UserId: id, ApiKey: apiKey, Enabled: !enabled || enabled.checked });
            }
            return profiles;
        }

        function load() {
            if (!mainApiKeyInput) {
                return;
            }

            Promise.all([ApiClient.getPluginConfiguration(pluginUniqueId), getUsers()]).then(function (values) {
                var config = values[0] || {};
                mediaUsers = values[1] || [];
                var profiles = normalizedProfiles(config);
                var mainUserId = currentUserId() || config.SyncUserId || "";
                var mainProfile = findProfile(profiles, mainUserId);
                var existingKey = mainProfile && (mainProfile.ApiKey || mainProfile.apiKey) ? (mainProfile.ApiKey || mainProfile.apiKey) : (config.ApiKey || "");
                mainApiKeyInput.value = "";
                mainApiKeyInput.placeholder = existingKey ? "已配置，留空保持不变" : "cpk_…";
                mainApiKeyInput.setAttribute("data-existing-key", existingKey);
                if (reverseSyncInput) {
                    reverseSyncInput.checked = config.ReverseSyncEnabled !== false;
                }
                renderProfiles(profiles.filter(function (profile) {
                    return !mainUserId || String(profile.UserId || profile.userId || "").toLowerCase() !== mainUserId.toLowerCase();
                }));
            }).catch(function (error) {
                console.error("CinePersona 配置读取失败", error);
                setStatus("读取配置失败，请刷新后重试。", "error");
            });
        }

        function submit(event) {
            if (event) {
                event.preventDefault();
                event.stopPropagation();
            }

            if (!mainApiKeyInput) {
                return false;
            }

            if (saveButton) {
                saveButton.disabled = true;
            }
            setStatus("正在保存…", "saving");

            ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
                var mainKey = String(mainApiKeyInput.value || "").trim() || mainApiKeyInput.getAttribute("data-existing-key") || "";
                var mainUserId = currentUserId() || config.SyncUserId || "";
                if (!mainKey) {
                    throw new Error("请填写主账号 API Key。");
                }
                if (!mainUserId) {
                    throw new Error("无法识别当前管理员账号，请刷新页面后重试。");
                }
                if (reverseSyncInput) {
                    config.ReverseSyncEnabled = reverseSyncInput.checked;
                }
                var profiles = collectProfiles().filter(function (profile) {
                    return String(profile.UserId || "").toLowerCase() !== mainUserId.toLowerCase();
                });
                profiles.push({ UserId: mainUserId, ApiKey: mainKey, Enabled: true });
                config.ApiKey = mainKey;
                config.SyncUserId = mainUserId;
                config.UserProfiles = profiles;
                return ApiClient.updatePluginConfiguration(pluginUniqueId, config);
            }).then(function () {
                setStatus("已保存，插件会使用新配置。", "success");
            }).catch(function (error) {
                console.error("CinePersona 配置保存失败", error);
                setStatus(error && error.message ? error.message : "保存失败，请检查配置后重试。", "error");
            }).then(function () {
                if (saveButton) {
                    saveButton.disabled = false;
                }
            });

            return false;
        }

        page.addEventListener("viewshow", initialize);
        initialize();
    };
});
