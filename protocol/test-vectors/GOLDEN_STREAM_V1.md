# TermLink Golden Stream Bundle V1

状态：阶段 0 测试格式，可用于固定回归数据；不是网络协议或跨设备快照格式。

## 1. 目标

Golden Stream Bundle 无损保存一次 ConPTY 会话中观察到的输出块、终端尺寸和生命周期
顺序。Android Harness、Termux Fork 和 xterm.js 必须消费同一 Bundle，不能先把输出按
行或 UTF-8 字符串重组。

V1 使用一个目录作为 Bundle：

```text
<case-name>/
├── manifest.json
├── events.jsonl
├── output.bin
└── expectations.json
```

所有 JSON 文件使用 UTF-8（无 BOM）。`output.bin` 是按观察顺序拼接的原始 ConPTY
字节；不得执行换行、编码或控制字符规范化。

## 2. Manifest

`manifest.json` 的稳定字段如下：

```json
{
  "format": "termlink.golden-stream",
  "format_version": 1,
  "capture_id": "UUID",
  "case_name": "smoke-powershell-v1",
  "created_at": "RFC 3339 timestamp",
  "clock": "monotonic-microseconds",
  "source": {
    "platform": "windows-conpty",
    "shell": "powershell",
    "command_line": "powershell.exe -NoLogo -NoProfile"
  },
  "initial_size": { "columns": 80, "rows": 25 },
  "files": {
    "events": "events.jsonl",
    "output": "output.bin",
    "expectations": "expectations.json"
  },
  "statistics": {
    "event_count": 5,
    "output_event_count": 1,
    "output_bytes": 128
  },
  "sha256": {
    "events": "lowercase hex",
    "output": "lowercase hex",
    "expectations": "lowercase hex"
  }
}
```

哈希覆盖文件的精确字节。读取器必须先校验格式版本、文件名和哈希，再回放正文。

## 3. Event Stream

`events.jsonl` 每行是一个事件。所有事件包含：

- `seq`：从 1 开始严格连续的事件序号；
- `elapsed_us`：从本次捕获开始计算的单调微秒数，必须非递减；
- `type`：`output`、`resize` 或 `lifecycle`。

输出事件引用 `output.bin` 的连续范围：

```json
{"seq":3,"elapsed_us":12000,"type":"output","data_offset":0,"data_length":42}
```

V1 要求输出范围按事件顺序连续、无重叠、无空洞，并最终恰好覆盖整个
`output.bin`。读取器必须按 `data_length` 取原始字节，即使范围落在 UTF-8 多字节字符
或 VT 控制序列中间也不能重新分块。

Resize 事件：

```json
{"seq":2,"elapsed_us":9000,"type":"resize","columns":80,"rows":25,"reason":"initial"}
```

生命周期事件：

```json
{"seq":1,"elapsed_us":8000,"type":"lifecycle","state":"started"}
{"seq":5,"elapsed_us":50000,"type":"lifecycle","state":"ended","exit_code":17}
```

V1 Bundle 必须以唯一的 `started` 开始，并以唯一的 `ended` 结束。`exit_code` 只在
`ended` 中出现。

## 4. Expectations

`expectations.json` 保存测试预期，不参与生产协议：

```json
{
  "exit_code": 17,
  "final_size": { "columns": 100, "rows": 30 },
  "output_contains_utf8": ["__OUTPUT_CAPTURE_OK__"],
  "normalized_grid": null,
  "reference_screenshot": null
}
```

`output_contains_utf8` 只用于明确声明为文本的测试标记。普通读取路径不得因为存在该
字段而把网络层或录制层输出转换成字符串。`normalized_grid` 保留为空，直到 Termux 与
xterm.js 对照完成后再设计独立版本。

## 5. 演进规则

- V1 读取器遇到未知 `format_version` 必须拒绝，不能猜测；
- 可选预期结果可以扩展，但事件字段的语义变化必须提升格式版本；
- Terminal Engine 私有快照不得写入 Bundle 的通用事件；
- 输入正文默认不录制，避免把命令历史固化进测试或生产记录；
- Bundle 可以公开提交前必须检查路径、用户名、Token 和其他敏感输出。
