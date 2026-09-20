# DeepSeek AI（`io.github.kevendai.ai-deepseek`）

v2.1 官方 **AI Provider**，实现 `ai_provider@1`。规格：`docs/V2.1-PROVIDER-INTERFACE.md` §7.2。

## 它做什么 / 不做什么

**只做模型调用**：endpoint、API Key、model、超时、重试、最大并发、请求与返回格式、厂商错误解析。

**不知道**什么是 arXiv、不知道 prompt 的业务含义、不知道"两阶段评分"（标题批量 → 阈值筛选 →
摘要 → 排序 → Top-N）。那些业务规则留在消费方（arxiv）。⇒ 换掉本插件不会改变任何评分算法。

## 提供的 action

| action | input | output |
| --- | --- | --- |
| `structured_complete` | `{messages:[{role,content}], response_schema:{…}, purpose:"arxiv_title_scoring"}` | `{json:{…}, usage:{prompt_tokens,completion_tokens}}` |
| `test_connection` | `{}` | `{message:"模型可用", model, reply, usage, limits}` |
| `validate_settings` | `{}` | `{message:"设置有效", api_url, model, limits}` |

- `messages[].role` 只接受 `system` / `user` / `assistant`，`content` 不得为空；最多 64 条。
- `purpose` 只用于日志标签，本插件**不解释**它；格式限 `^[a-z0-9_]{1,64}$`。
- `response_schema` 只做**格式级**检查：若声明了顶层 `required` 字段，返回的 JSON 缺一个就报错。
  不解释 `properties` 的业务含义，更不做分数区间校验（那是 PaperBundle 校验器的事）。
- **`limits.max_concurrency` 是消费方要读的**：`max_concurrency` 是本 provider 的设置，但并发批次是
  消费方自己开的。消费方在开始一轮之前调用一次 `validate_settings`（或 `test_connection`）即可拿到
  `limits`，不需要额外往返。

## 错误语义（从 2.0.4 的本体搬进来，不是重写）

| 情形 | 行为 |
| --- | --- |
| HTTP 401 | `ok:false` + **`fatal:true`** + 「API Key 无效或已被撤销」 |
| HTTP 402 | `ok:false` + **`fatal:true`** + 「账户余额不足，请充值后重新同步论文」 |
| HTTP 403 | `ok:false` + **`fatal:true`** + 「拒绝了本次请求，请检查账号权限或所在地区限制」 |
| 未配置 API Key | `ok:false` + **`fatal:true`** + 「请先在设置里填写 API Key」 |
| HTTP 429 / 500 / 503 | **内部有限重试**（2s / 4s / 8s，各带抖动；共 3 次），仍失败则 `ok:false` + `fatal:false` |
| 其他 4xx | `ok:false` + `fatal:false` |
| 连接失败 / 超时 / DNS | `ok:false` + `fatal:false` + 可读中文 |
| 返回的 `content` 不是 JSON 对象 | `ok:false` + `fatal:false` + 「返回的 content 不是合法 JSON」 |
| 返回缺 `response_schema.required` 字段 | `ok:false` + `fatal:false` + 点名缺哪些字段 |

`fatal:true` 由插件放进自己的 `payload`，**Broker 会把它提升成响应信封的顶层字段**
（`PluginHostApp.ServiceEnvelope` 里的 `{"fatal",fatal}`），消费方读的是**顶层** `fatal`
而不是 `output.fatal`；失败时 `output` 为 `null`。拿到 `error_kind:"provider_error"` +
`fatal:true` 后消费方**立即中止整轮**，不会像 2.0.4 之前那样对每一批白跑一遍
（33 批 × 重试 ≈ 1400 次注定失败的请求）。

`AggregateException` 会被 `Flatten()` 拍平后再取消息，避免界面只剩「发生一个或多个错误。」。

### 与文件服务器类插件的关键差异

`paper-snapshot-sync` 连的是**局域网**服务，所以无条件显式 `request.Proxy = null`；
本插件连的是**公网** `api.deepseek.com`，**沿用系统代理**。但**回环 / RFC1918 内网 /
IPv6 link-local** 的目标地址会**直连**（`IsLocalEndpoint`）——理由和前者一样：内网地址被
代理客户端劫持后行为不可预测。顺带这也让「把 `api_url` 指向局域网里自建的 OpenAI 兼容
服务（vLLM / LM Studio 等）」这种用法能正常工作，并且让离线 CI 探针不依赖本机代理设置。

## 设置项

| 键 | 说明 | 默认 |
| --- | --- | --- |
| `api_url` | Chat Completions 地址 | `https://api.deepseek.com/chat/completions` |
| `api_key` | API Key（`x-secret`，DPAPI 加密存 `secret.dat`） | 空 |
| `api_model` | 模型 | `deepseek-v4-flash` |
| `timeout_seconds` | HTTP 超时（高级） | `180` |
| `max_concurrency` | 最大并发（高级） | `8` |

键名**刻意与 arxiv 的旧字段同名**（`api_url` / `api_key` / `api_model` / `timeout_seconds` /
`max_concurrency`），这样 §9.1/§9.2 的迁移就是一次直拷，回退 arxiv 1.0.2 时旧配置也还在
（迁移**复制不删除**）。

`thinking:{"type":"enabled"}` 与 `response_format:{"type":"json_object"}` 与 2.0.4 的行为一致；
返回内容若被 ```` ```json ```` 代码块包住会先剥壳再解析（格式层容错）。

## 离线自检

插件 EXE 直接跑 `AiProviderSelfTest`（退出码 70 起），不联网：请求体构造、消息与 `purpose`
校验、401/402/403 文案映射、usage 缺失回零、代码块剥壳、`response_schema.required` 检查、
超时/并发夹取、`AggregateException` 拍平、回环/内网直连判定。全链路（真 HTTP + Broker）由
`tests/AiDeepSeekProbe.cs` 用 TcpListener 版假服务器覆盖。
