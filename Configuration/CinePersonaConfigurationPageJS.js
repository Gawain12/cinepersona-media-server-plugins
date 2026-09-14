define([], function () {
    return function (page) {
        var pluginUniqueId = "f62e8471-469b-43d8-b57f-f4a4d7d10001";
        var form;
        var apiKeyInput;
        var serverUrlInput;
        var syncUserIdInput;
        var reverseSyncInput;
        var saveButton;
        var status;
        var initialized = false;

        function initialize() {
            if (initialized) {
                return;
            }

            form = page.querySelector("#cinepersonaConfigurationForm");
            apiKeyInput = page.querySelector("#txtApiKey");
            serverUrlInput = page.querySelector("#txtServerUrl");
            syncUserIdInput = page.querySelector("#txtSyncUserId");
            reverseSyncInput = page.querySelector("#chkReverseSync");
            saveButton = page.querySelector("#btnSave");
            status = page.querySelector("#cinepersonaSaveStatus");

            if (!form || !apiKeyInput || !serverUrlInput) {
                return;
            }

            initialized = true;
            form.addEventListener("submit", submit);
            load();
        }

        function setStatus(message, state) {
            if (!status) {
                return;
            }

            status.textContent = message || "";
            status.setAttribute("data-state", state || "");
        }

        function load() {
            if (!apiKeyInput || !serverUrlInput) {
                return;
            }

            ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
                apiKeyInput.value = "";
                serverUrlInput.value = config.ServerUrl || "https://cinepersona.com";
                if (syncUserIdInput) {
                    syncUserIdInput.value = config.SyncUserId || (ApiClient.getCurrentUserId ? ApiClient.getCurrentUserId() : "");
                }
                if (reverseSyncInput) {
                    reverseSyncInput.checked = config.ReverseSyncEnabled !== false;
                }
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

            if (!apiKeyInput || !serverUrlInput) {
                return false;
            }

            if (saveButton) {
                saveButton.disabled = true;
            }
            setStatus("正在保存…", "saving");

            ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
                var enteredApiKey = apiKeyInput.value.trim();
                if (enteredApiKey) {
                    config.ApiKey = enteredApiKey;
                }
                if (!config.ApiKey) {
                    throw new Error("API Key 不能为空");
                }
                config.ServerUrl = serverUrlInput.value;
                if (syncUserIdInput) {
                    config.SyncUserId = syncUserIdInput.value.trim();
                }
                if (reverseSyncInput) {
                    config.ReverseSyncEnabled = reverseSyncInput.checked;
                }
                return ApiClient.updatePluginConfiguration(pluginUniqueId, config);
            }).then(function () {
                setStatus("已保存，插件会使用新配置。", "success");
            }).catch(function (error) {
                console.error("CinePersona 配置保存失败", error);
                setStatus(error && error.message === "API Key 不能为空"
                    ? "请输入 API Key 后再保存。"
                    : "保存失败，请检查地址和 API Key 后重试。", "error");
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
