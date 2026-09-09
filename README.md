# CinePersona Media Server Plugins

CinePersona 的媒体服务器同步插件集合：电影播放完成度达到 80% 后，自动将观影记录同步到 CinePersona。

当前包含：

- `Emby.Plugin.CinePersona`：Emby 原生 C# 插件。
- `jellyfin/Jellyfin.Plugin.CinePersona`：Jellyfin 原生 C# 插件。
- `plex/CinePersona.PlexRelay`：Plex Webhook companion relay。Plex 官方目前没有等价的稳定原生服务端播放事件插件接口，因此这个组件负责接收 Plex Webhook 并转发到 CinePersona；它不绕过 Plex Pass 限制。

插件发送到 CinePersona 的实际 API 路径是 `/v1/webhook/emby`、`/v1/webhook/jellyfin` 和 `/v1/webhook/plex`；服务器地址只需要填写 `https://cinepersona.com` 或 `https://test.gawyn.de`。

## Emby 支持内容

- Emby Server 4.7+
- 仅处理 `Movie`
- 仅在 `PositionTicks / RunTimeTicks >= 0.8` 时同步
- 自动携带 TMDB ID、IMDb ID、片名和年份
- 支持 CinePersona 正式环境和测试环境

## 安装

1. 在 CinePersona 中创建 API Key，并授予 `sync.write` 权限。
2. 从 GitHub Releases 下载 `Emby.Plugin.CinePersona.dll`。
3. 将 DLL 放入 Emby Server 的 `plugins` 目录。
4. 重启 Emby Server。
5. 在 Emby 后台的插件设置中填写 API Key 和 CinePersona 地址。

默认地址：`https://cinepersona.com`

测试地址：`https://test.gawyn.de`

## 从源码构建

```bash
dotnet build Emby.Plugin.CinePersona.csproj -c Release
dotnet build jellyfin/Jellyfin.Plugin.CinePersona/Jellyfin.Plugin.CinePersona.csproj -c Release
cd plex/CinePersona.PlexRelay
go test ./...
```

Emby 输出文件位于：

`bin/Release/netstandard2.0/Emby.Plugin.CinePersona.dll`

Jellyfin 和 Plex 的说明分别见 [jellyfin/README.md](jellyfin/README.md) 与 [plex/README.md](plex/README.md)。

## 隐私与安全

插件只向配置的 CinePersona 地址发送观影事件和媒体识别信息，不会抓取 IMDb 或其他外部网站，也不会上传视频文件。
