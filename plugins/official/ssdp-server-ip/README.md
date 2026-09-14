私人插件，不随主程序捆绑；请从插件市场按需安装。

# SSDP 服务器 IP 同步

通过 UDP 单播 SSDP M-SEARCH，在用户配置的 IPv4 地址和子网掩码范围内发现设备。

首次配置会完整扫描并按 `SERVER / USN / IP` 列出设备；保存后以完整 USN 为身份，后续先探测上次 IP，未命中才扫描配置范围。插件输出 `server_ip`、`server`、`usn` 和 `location`，可通过 `{{plugin:io.github.kevendai.ssdp-server-ip:server_ip}}` 引用。

此插件随主程序提供，但不在公开插件市场中展示。
