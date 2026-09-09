# Jellyfin.Plugin.CinePersona

Jellyfin 原生服务端插件：电影播放完成度达到 80% 后，自动将观影记录同步到 CinePersona。

项目按 Jellyfin 官方插件模板使用 `net9.0` 和 Jellyfin `10.11.5` 共享包；共享包仅作为编译期依赖，不会复制进插件发布目录。Jellyfin 官方文档要求插件包版本与服务器版本匹配，否则插件可能显示为不受支持。

## 构建

```bash
dotnet build Jellyfin.Plugin.CinePersona/Jellyfin.Plugin.CinePersona.csproj -c Release
```

## 安装

将生成的 DLL 放入 Jellyfin 的插件目录下以插件名命名的子目录，重启 Jellyfin，然后在插件设置中填写 CinePersona API Key 和服务器地址。

API Key 只需要 `sync.write` 权限。
