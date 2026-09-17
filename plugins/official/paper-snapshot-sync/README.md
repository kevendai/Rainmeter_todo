# Paper Snapshot Sync

v2.1 官方 Provider 插件（`paper_snapshot_provider@1`，免费）。把评分完成的论文推荐快照
（PaperBundle v1）上传到文件服务器（File Browser），并在快照缺失时按 `(日期, 配置指纹)`
取回 —— 命中即可跳过 AI 评分直接导入。

- 只负责存储与取回；不负责 arXiv 抓取、AI 评分、翻译或 Todo 导入。
- 远端布局 `paper/<日期>-<profile_hash 前 12 位>.json`：同一配置重复上传为幂等覆盖，
  不同配置互不干扰；旧版 arxiv 1.0.2 的 `paper/<日期>_papers.json` 不读取也不改动。
- `found:false`（服务器明确没有）与 `ok:false`（请求失败）严格区分。
- 地址可被地址插件（如 SSDP 服务器 IP 同步）以 `paper_snapshot.file_server` 目标代管，
  只替换主机，端口与路径保留。
