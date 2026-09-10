# CinePersona Plex Relay

Plex 目前没有与 Emby/Jellyfin 等价的、可稳定拦截服务端播放事件的原生服务端插件接口；Plex 官方提供的是 Webhooks，且官方文档明确说明 Webhook 需要 Plex Pass。

因此本目录提供一个可部署的 Plex companion relay：接收 Plex 的 `media.scrobble`（电影观看完成）和 `media.rate`（电影个人评分）multipart webhook，保留 Plex 的媒体识别信息，再转发到 CinePersona 的 `/v1/webhook/plex`。它只处理电影；电视剧等类型会被忽略。它适合已经具备 Plex Webhooks 权限的用户；它不会伪装成一个 Plex 原生插件，也不会绕过 Plex Pass 限制。

Plex Webhook 会带电影评分，但不会可靠提供 CinePersona 所需的评论正文。评论仍可在 CinePersona 的电影编辑弹窗中填写；评分同步不会覆盖已有评论或产生重看记录。

## 运行

```bash
cp .env.example .env
# 编辑 .env，至少填写 CINEPERSONA_API_KEY
go run .
```

把 Plex Webhook URL 配置为：

```text
https://你的域名/webhook?secret=PLEX_WEBHOOK_SECRET
```

也可以使用 Docker Compose：

```bash
docker compose -f docker-compose.example.yml up -d --build
```

## 后续方向

如果目标是完全不依赖 Plex Pass，下一阶段应做 Plex polling agent：用 Plex server token 定期读取正在播放会话，观察会话结束并按完成度写入 CinePersona。这是独立 agent，不是 Plex 原生插件，且需要处理多用户映射、会话丢失和轮询间隔等问题。
