# 腾讯云翻译（`io.github.kevendai.translate-tencent`）

v2.1 官方 **翻译 Provider**，实现 `translation_provider@1`。规格：`docs/V2.1-PROVIDER-INTERFACE.md` §7.3。
`billing: "may_charge"` —— 腾讯云机器翻译（TMT）按字符计费，每月有免费额度，用完继续调用会产生费用。

## 它做什么 / 不做什么

**只做"把一批文本翻成另一种语言"**：TC3 签名、批量请求、内部分批、限流、厂商错误码解析、结果缓存。

**不知道**这些文本是论文标题、不知道"只翻最终 Top-N"、不知道评分规则。那些业务规则留在消费方
（arxiv）。⇒ 换掉本插件（换一家翻译厂商）不会改变任何业务行为。

## 提供的 action

| action | input | output |
| --- | --- | --- |
| `translate` | `{texts:[…], source_language:"en", target_language:"zh-CN"}` | `{translations:[…], source_language, target_language, usage:{translated_chars,requests,cached}, limits}` |
| `test_connection` | `{}` | `{message:"翻译服务可用", source_language, target_language, sample:{text,translation}, region, limits}` |
| `validate_settings` | `{}` | `{message:"设置有效", endpoint, region, source_language, target_language, cache_enabled, cache_entries, limits, notes}` |

- `translations[i]` 与 `texts[i]` **严格一一对应且保序**；长度不一致时插件**拒绝这一批结果**并报错，
  绝不返回一个短数组让消费方错位（§7.3 的 `protocol_error` 语义）。
- `texts` 单次上限 `max_texts_per_call`（默认 200，硬上限 500）；单条 ≤ 2000 字符（腾讯云硬限制）；
  单次调用合计 ≤ 40000 字符。超限**一次请求都不发**，报可读中文让消费方分批。
- 单个请求内部还会按 1800 字符预算**再分批**（官方口径是"低于 2000"，留 10% 余量），
  并按 250ms 间隔节流（腾讯云默认 5 次/秒）。
- 源语言与目标语言相同 ⇒ **直接原样返回，一次请求都不发**（不花这笔钱）。
- 语言换算表在 Provider 里：消费方用 BCP-47（`zh-CN` / `zh-TW` / `en` / `ja` / `en-US`），
  本插件负责换成腾讯云短码（`zh` / `zh-TW` / `en` / `ja`）。消费方不该知道任何一家厂商的语言代码。

## 错误语义

| 情形 | 行为 |
| --- | --- |
| `AuthFailure.*`（签名错 / Secret ID 不存在 / 过期） | `ok:false` + **`fatal:true`** + 可读中文 |
| `UnauthorizedOperation.*`（未开通 TMT / CAM 无权限） | `ok:false` + **`fatal:true`** |
| `FailedOperation.NoFreeAmount`（免费额度用完） | `ok:false` + **`fatal:true`** + 「本月免费额度已用完」 |
| `FailedOperation.ServiceIsolate`（欠费停服） | `ok:false` + **`fatal:true`** |
| `UnsupportedOperation.*`（语言不支持 / 文本超长） | `ok:false` + **`fatal:true`** |
| 未填 Secret ID / Secret Key | `ok:false` + **`fatal:true`**，一次请求都不发 |
| 语言代码不在支持列表 | `ok:false` + **`fatal:true`**，一次请求都不发 |
| `RequestLimitExceeded.*` / `LimitExceeded.*` / `InternalError.*` | **内部有限重试**（1s / 2s / 4s，各带抖动），仍失败则 `ok:false` + `fatal:false` |
| HTTP 401 / 403 | `ok:false` + **`fatal:true`**，不重试 |
| HTTP 429 / 5xx | 内部重试，耗尽后 `ok:false` + `fatal:false` |
| 返回不是合法 JSON / 没有 `Response` / 缺 `TargetTextList` | `ok:false` + `fatal:false` |
| 返回译文条数与请求条数不一致 | `ok:false` + `fatal:false`，文案点名"长度不一致"，这一批结果整体丢弃 |

`fatal:true` 由插件放进自己的 `payload`，**Broker 会把它提升成响应信封的顶层字段**，
消费方读**顶层** `fatal`；拿到 `fatal:true` 后应立即中止整轮，不要对每一批白跑一遍。

`AuthFailure.SignatureExpire` 的文案会提醒"本机时间与标准时间相差超过 5 分钟"——
TC3 的 `Timestamp` 参与签名，本机时钟飘了以后所有调用都会失败，这是最常见的一个坑。

## 设置项

| 键 | 说明 | 默认 |
| --- | --- | --- |
| `secret_id` | 腾讯云 Secret ID | 空 |
| `secret_key` | 腾讯云 Secret Key（`x-secret`，DPAPI 加密存 `secret.dat`） | 空 |
| `source_language` | 源语言（`auto` = 自动识别） | `auto` |
| `target_language` | 目标语言 | `zh` |
| `api_endpoint` | 接口地址（高级） | `https://tmt.tencentcloudapi.com` |
| `region` | 地域（高级） | `ap-guangzhou` |
| `timeout_seconds` | HTTP 超时（高级，夹取 10~600） | `60` |
| `max_texts_per_call` | 单次最多条数（高级，夹取 1~500） | `200` |
| `use_local_cache` | 复用本地翻译缓存（高级） | `true` |

## 本地缓存（§7.3）

结果按 `sha256("v1\n" + source + "\n" + target + "\n" + text)` 落
`PluginData\io.github.kevendai.translate-tencent\translation-cache.json`。
键里带上语言对（规格原话是 `sha256(title)`）是因为同一个标题翻成不同语言是两份不同的结果，
只用 `sha256(title)` 会串味。缓存最多保留 5000 条，按 `updated_at` 淘汰最旧的。

缓存读写**任何情况下都不影响翻译本身**：读失败当没有缓存，写失败只记一行 stderr；
文件损坏时把它挪成 `translation-cache.json.corrupt`，本次调用仍会把新结果写回，
所以**重翻一次**就恢复命中 —— 留着坏文件会让缓存永久失效，「重评不重复付费」也就作废了。

## 网络与代理

公网接口 `tmt.tencentcloudapi.com` **沿用系统代理**；回环 / RFC1918 内网 / IPv6 link-local 一律**直连**
（`IsLocalEndpoint`）。理由与 ai-deepseek 相同：内网地址被代理客户端劫持后行为不可预测；
顺带也让离线探针（指着 `127.0.0.1`）不依赖本机代理设置。

## 离线自检

插件 EXE 直接跑 `TranslationProviderSelfTest`（退出码 70 起，不联网），覆盖：
TC3 签名的**腾讯云官方已知答案**（payload 哈希 `35e9c5b0…`、CanonicalRequest 哈希 `7019a55b…`、
`HMAC(kSigning, StringToSign)` = `10b1a37a…`，全部逐字节比对）、UTC 日期、
Authorization 头形状、语言换算表、输入校验边界、内部分批、响应解析、厂商错误码分级、
缓存键与坏缓存隔离、配置夹取与地址规范化。

全链路（真 HTTP + 真 Broker + 真 TC3 验签）由 `tests/TranslateTencentProbe.cs` 用
TcpListener 版假 TMT 端点覆盖：探针会**独立重算一遍签名**并与请求头逐字节比对。
