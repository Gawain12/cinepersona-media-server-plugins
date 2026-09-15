# Jellyfin.Plugin.CinePersona

Jellyfin 原生服务端插件：电影播放完成度达到 80%、手动标记为看过或提交个人星级评价后，自动将观影记录同步到 CinePersona。

Jellyfin Web 电影详情页还会注入一个“评分”入口：可提交评分和短评，已有评分会回显在按钮上。短评不会写入 Jellyfin 原生评论字段，也不会把 API Key 暴露给浏览器。

当前仅同步 `Movie`。电视剧、季度、单集、音乐、书籍和其他类型会被跳过，不会被强制当作电影提交。

当前发布包针对 Jellyfin `10.10.x` 使用 .NET 8 和 Jellyfin `10.10.7` 共享包；共享包仅作为编译期依赖，不会复制进插件发布目录。Jellyfin 官方文档要求插件包版本与服务器版本匹配，否则插件可能显示为不受支持。

## 构建

```bash
dotnet build Jellyfin.Plugin.CinePersona/Jellyfin.Plugin.CinePersona.csproj -c Release
```

## 安装

在 Jellyfin 控制台 → 插件 → 存储库中添加：

`https://cinepersona.com/jellyfin.json`

然后在插件目录中安装 CinePersona。也可以使用 GitHub Release 中的 ZIP 包手动安装。

安装后重启 Jellyfin，然后打开 Jellyfin Web 的电影详情页并硬刷新一次，操作栏会出现“评分”按钮，已有评分显示为 `7/10` 形式；手机、电视等原生客户端暂不注入此按钮。

API Key 只需要 `sync.write` 权限。
