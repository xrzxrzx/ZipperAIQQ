# C# 编码规范

> 适用范围：ZipperAIQQ 全部 C# 代码（服务端 + 客户端）
> 基线：**.NET 10 / C# 14**
> 目标：**让代码在最容易出事的地方（并发、异步、资源、契约）不出事**
>
> 本规范不是教科书，是**故障驱动的清单**——每条后面标注的「教训」都来自真实踩过的坑（教训台账属项目内部资料，不随仓库公开）。

---

## 0. 总则

1. **可读性优先于小聪明**：读代码的时间远多于写。
2. **显式优于隐式**：不要靠约定俗成的副作用传递信息。
3. **失败要响**：宁可启动时报错崩掉，也不要静默降级跑出错误结果。
4. **不猜**：不确定的 API 行为先写最小验证，别靠"应该可以"。

---

## 1. 命名

| 元素 | 规则 | 示例 |
|---|---|---|
| 命名空间 / 类 / 接口 / 方法 / 属性 | **PascalCase** | `QqChannelAdapter`、`SendMessageAsync` |
| 接口 | 前缀 `I` | `IQQChannel`、`IMessageStore` |
| 局部变量 / 参数 | **camelCase** | `messageId`、`groupId` |
| 私有字段 | **`_camelCase`** | `_httpClient`、`_logger` |
| 常量 | PascalCase | `MaxRetryCount` |
| 静态只读字段 | PascalCase | `DefaultTimeout` |
| 异步方法 | 后缀 **`Async`** | `LoginAsync`、`FetchAsync` |
| 类型参数 | `T` + 描述 | `TResult` |

- **禁止**拼音命名、无意义缩写（`tmp`/`data1`/`obj2`）。`msg`、`ctx`、`cfg` 这类广泛认可的缩写可以。
- 布尔量用 `Is` / `Has` / `Can` / `Should` 开头。

---

## 2. 文件与类型组织

- **一个文件一个公开类型**，文件名 = 类型名。
- `using` 放在文件顶部（**不要**放 namespace 内）。
- 命名空间用**文件范围声明**（C# 10+）：
  ```csharp
  namespace ZipperAIQQ.Server.Core;   // ✅
  ```
- 成员顺序：常量 → 字段 → 构造函数 → 属性 → 公开方法 → 私有方法。
- 启用 `<Nullable>enable</Nullable>` 与 `<ImplicitUsings>enable</ImplicitUsings>`。

---

## 3. 异步编程 ⚠️ 重点

### 3.1 禁止 `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`

```csharp
// ❌ 在 UI 线程或有 SynchronizationContext 的地方会死锁
var resp = client.SendAsync(req).Result;

// ✅
var resp = await client.SendAsync(req);
```

> **教训 #1**：旧项目 `NapCat.SendAPI()` 用 `.Result` 同步等 → UI 线程死锁隐患。

- **例外**：`Main` 方法可以直接 `await`（C# 7.1+ 支持 `async Task Main`）。

### 3.2 禁止 `async void`

```csharp
// ❌ 异常无法被捕获，进程可能静默出错
private async void Handle(BotMessageEvent e) { ... }

// ✅ 返回 Task；若必须做事件处理器，自己包一层 try/catch
private async Task HandleAsync(BotMessageEvent e) { ... }
```

> **教训 #2**：旧项目 `QQLifeAssistant.Handle` 是 `async void`。

### 3.3 事件处理器若必须 `async void`，**必须**整体 try/catch

```csharp
private async void OnSomeEvent(object? sender, EventArgs e)
{
    try { await DoWorkAsync(); }
    catch (Exception ex) { _logger.LogError(ex, "处理事件失败"); }   // 不能让它逃逸
}
```

### 3.4 长跑循环里捕获异常要**带退避**

```csharp
// ❌ 连接断开后死循环刷日志
while (true) { try { await ReceiveAsync(); } catch (Exception ex) { _logger.LogError(ex, ""); } }

// ✅ 区分可恢复异常 + 退避 + 上限
var delay = TimeSpan.FromSeconds(1);
while (!ct.IsCancellationRequested)
{
    try { await ReceiveAsync(ct); delay = TimeSpan.FromSeconds(1); }
    catch (OperationCanceledException) { break; }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "接收失败，{Delay} 后重试", delay);
        await Task.Delay(delay, ct);
        delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));   // 指数退避，封顶
    }
}
```

> **教训 #4**：旧项目 `NapCatWebSocket.Recveive` 的 catch 在 while 内且无退避 → 断线后刷屏。

### 3.5 库代码用 `ConfigureAwait(false)`；UI 代码**不要**用

- **服务端 / 类库**：`await xxx.ConfigureAwait(false);`
- **WPF 客户端**：**不要**加（需要回到 UI 线程更新控件）

### 3.6 所有异步 API 都要能取消

公开的异步方法尽量接受 `CancellationToken ct = default`，并**向下传递**。

---

## 4. 并发与线程安全 ⚠️ 重点

### 4.1 跨线程共享的集合必须用并发容器

```csharp
// ❌ 会被多线程并发读写（旧项目正是这么写的）
private readonly Dictionary<string, RequestWaiter> _waiters = new();

// ✅
private readonly ConcurrentDictionary<string, RequestWaiter> _waiters = new();
```

> **教训 #3**：旧项目 `NapCatAPI.requestWaiters` 是普通 `Dictionary`，被 ThreadPool 线程并发访问 → 竞态。

### 4.2 请求-响应配对优先用 `TaskCompletionSource`

```csharp
// ✅ 异步等待，不阻塞线程
var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
_pending[echo] = tcs;
await SendAsync(payload, ct);
var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
```

> **教训**：旧项目用 `ManualResetEvent` 阻塞等待（源码里自己标了 `//TODO 优化成异步方法`）。

### 4.3 无界的 `Task.Run` / `ThreadPool.QueueUserWorkItem` 是危险的

消息风暴下会打爆线程池。**必须有并发上限**（`SemaphoreSlim`、`Channel` + 固定消费者、或 `Parallel.ForEachAsync` 的 `MaxDegreeOfParallelism`）。

### 4.4 不要在 `lock` 里 `await`

`lock` 块内不能 `await`；需要异步互斥用 `SemaphoreSlim(1,1)`。

### 4.5 状态变更要考虑可见性与原子性

`int` 计数器的 `++` **不是**原子操作——用 `Interlocked.Increment`。

---

## 5. 异常处理

- **不要吞异常**。`catch { }` 一律禁止；`catch (Exception) { }` 必须至少记日志。
- **区分异常类型**：`OperationCanceledException` 是**正常流程**（取消），不要当错误记。
- **不要用异常做控制流**。
- **自定义异常**继承 `Exception`，命名以 `Exception` 结尾，并提供有意义的 message。
- **跨边界（HTTP/WS 响应）不要抛原始异常**：转换为业务错误码 + 可读 message（契约是 `{code, message, data}`）。
- **捕获范围要精确**：能捕获具体类型就别捕获 `Exception`。

```csharp
// ❌
try { ... } catch { return null; }

// ✅
try { ... }
catch (HttpRequestException ex) { _logger.LogWarning(ex, "签名服务不可达"); throw; }
catch (JsonException ex) { _logger.LogError(ex, "签名服务响应格式异常"); throw; }
```

---

## 6. 资源管理

- `IDisposable` / `IAsyncDisposable` 对象一律 `using` 或 `await using`。
- **`HttpClient` 不要每次 `new`**：用 `IHttpClientFactory`，或至少做成 `static readonly` 单例。
- 长生命周期对象实现 `Dispose` 时，**必须真的释放**（连接、订阅、定时器）。

> **教训 #5**：旧项目 `Disconnect()` 条件判断写反，导致连接关不干净。

- 事件订阅记得取消（尤其是静态事件 → 内存泄漏）。

---

## 7. 日志

- 用 `ILogger<T>`（`Microsoft.Extensions.Logging`），**不要**自定义静态单例 logger。
- **用结构化占位符**，不要字符串拼接：
  ```csharp
  _logger.LogInformation("收到群 {GroupId} 的消息，长度 {Length}", groupId, text.Length);   // ✅
  _logger.LogInformation($"收到群 {groupId} 的消息");                                      // ❌
  ```
- 级别选择：`Trace` 只给协议帧级细节；`Debug` 给开发诊断；`Information` 给业务里程碑（登录成功、消息收发）；`Warning` 给可恢复异常；`Error` 给需要人介入的失败。
- **不要在热路径（每帧/每条消息）打 `Information` 以上级别**。
- **绝不记录机密**（token、密码、cookie、完整 API key）。

---

## 8. 配置

- 用 `IOptions<T>` / `IOptionsMonitor<T>` 绑定强类型配置，**不要**到处读字符串 key。
- **配置缺失要响**：必需项缺失 → **启动时抛异常**，不要用空字符串兜底。
  ```csharp
  // ❌ 静默失败（旧项目正是这么写的）
  public string this[string key] => _config.TryGetValue(key, out var v) ? v : string.Empty;

  // ✅ 需要的地方显式校验
  var token = _options.Value.Token;
  if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("未配置 Token");
  ```
  > **教训 #9**：旧项目配置索引器取不到 key 返回空串 → 静默失败难排查。
- 机密**不进仓库**：走环境变量或 `appsettings.Local.json`（已 gitignore）。

---

## 9. 空值处理

- 启用 nullable 后，**尊重编译器的警告**，不要用 `!` 强行消音。
- `?` 传播 + `??` 兜底可以，但**业务上"不该为空"的地方要显式抛**，别让它悄悄变 `null`。
- `FirstOrDefault` 之后**必须**判空。

```csharp
// ❌ 业务上必须存在的对象被静默忽略
var session = _sessions.FirstOrDefault(s => s.Id == id);
if (session is null) return;      // 调用方以为成功了

// ✅ 明确契约
var session = _sessions.FirstOrDefault(s => s.Id == id)
    ?? throw new KeyNotFoundException($"会话 {id} 不存在");
```

---

## 10. 集合与 LINQ

- 返回集合时优先 `IReadOnlyList<T>` / `IReadOnlyCollection<T>`，不要暴露可变内部集合。
- **LINQ 惰性求值**：`Where` 之后不 `ToList` 就可能在枚举时抛出，注意时序。
- 热路径避免 LINQ 分配（用 `for` + `Span` 或缓存的数组）。
- 高频查找用 `Dictionary`/`HashSet`，不要线性扫 `List`。

---

## 11. 依赖注入

- 服务端统一用 `Microsoft.Extensions.DependencyInjection`。
- **生命周期要分清**：`Singleton` 里**不能**注入 `Scoped`（会捕获作用域）。
- **不要用服务定位器**（`ServiceLocator.Get<T>()`）——构造函数注入优先。
- 后台任务实现 `IHostedService` / `BackgroundService`，配合 `IHostApplicationLifetime` 处理优雅关闭。

---

## 12. 注释与文档

- **公开 API 写 XML 文档注释**（`///`），说明**为什么**而不只是"是什么"。
- 注释解释**意图与坑**，不解释代码字面意思。
- 每个 `// TODO` 必须写清**要做什么 + 为什么现在没做**。
- 复杂算法 / 协议处理处，**贴协议字段来源或文档链接**。

---

## 13. 测试

- 用 **xUnit**。
- 命名：`MethodName_Scenario_ExpectedBehavior`，例如 `ParseAsync_EmptyMessage_ReturnsNull`。
- **AAA 结构**（Arrange-Act-Assert），一个测试一个断言主题。
- **必测的几类**：
  - 协议解析（尤其边界：空消息、超长消息、畸形 JSON）
  - 会话隔离（不同群/用户上下文不串）
  - **权限门禁**（非管理员触发危险操作必须被拒）——这是安全边界，必须有用例
  - 配置缺失时的行为（应当明确失败）
  - 断线重连与退避逻辑
- **不要测私有方法**（测行为，不测实现）。
- 对外部依赖（DeepSeek API、NapCat WS）用**接口 + 假实现**，不要打真实服务。

---

## 14. 项目结构约定（建议，可调整）

```
src/
├── ZipperAIQQ.Server/           # 服务端宿主（Kestrel，跨平台）
├── ZipperAIQQ.Core/             # 编排层 + agent 核心（跨平台类库，无 UI 依赖）
├── ZipperAIQQ.Channels/         # IQQChannel 实现（NapCat 正向/反向 WS）
├── ZipperAIQQ.Storage/          # SQLite 持久化
└── ZipperAIQQ.Client/           # WPF 客户端（Windows-only）
tests/
└── ZipperAIQQ.Core.Tests/
```

**约束**：`Server` / `Core` / `Channels` / `Storage` **不得引用任何 WPF/Windows 专有程序集**——这是"服务端保持跨平台"的物理保障。

---

## 附：本规范的来源

第 3、4、5、6、8 节的多条约束来自对既有代码的逐文件审查（10 条真实缺陷），逐条对应关系记录在项目内部的教训台账里（不随仓库公开）。
