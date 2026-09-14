define([], function () {
    return function (page) {
        var pluginUniqueId = "f62e8471-469b-43d8-b57f-f4a4d7d10001";
        var form;
        var serverUrlInput;
        var reverseSyncInput;
        var saveButton;
        var status;
        var initialized = false;

        function initialize() {
            if (initialized) {
                return;
            }

            form = page.querySelector("#cinepersonaConfigurationForm");
            serverUrlInput = page.querySelector("#txtServerUrl");
            reverseSyncInput = page.querySelector("#chkReverseSync");
            saveButton = page.querySelector("#btnSave");
            status = page.querySelector("#cinepersonaSaveStatus");

            if (!form || !serverUrlInput) {
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
            if (!serverUrlInput) {
                return;
            }

            ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
                serverUrlInput.value = config.ServerUrl || "https://cinepersona.com";
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

            if (!serverUrlInput) {
                return false;
            }

            if (saveButton) {
                saveButton.disabled = true;
            }
            setStatus("正在保存…", "saving");

            ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
                config.ServerUrl = serverUrlInput.value;
                if (reverseSyncInput) {
                    config.ReverseSyncEnabled = reverseSyncInput.checked;
                }
                return ApiClient.updatePluginConfiguration(pluginUniqueId, config);
            }).then(function () {
                setStatus("已保存，插件会使用新配置。", "success");
            }).catch(function (error) {
                console.error("CinePersona 配置保存失败", error);
                setStatus("保存失败，请检查 CinePersona 地址后重试。", "error");
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
