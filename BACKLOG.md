# 下一轮候选清单（Backlog）

本文只保留仍未完成、需要决策或压测的项。已完成项在 2026-11-26 从候选中移除：

- 折叠导致选中行消失时的选中态归属：Round 5 已完成（迁移到被折叠节点）。
- 提权重启保留搜索、折叠、视图模式和列排序：0.1.01 已完成。
- 命令行失败负缓存 30 秒 TTL：0.1.01 已完成。
- 端口从详情面板手动查询改为独立 2Hz 视图：2026-11-25 已完成。

## 总览

| # | 待办 | 来源 | 类型 | 建议顺序 |
|---|---|---|---|---|
| 1 | 兄弟节点稳定顺序 | `docs/design/archive/design-round2.md` §11.2 | 观测后决策 | 3 |
| 2 | 键盘导航与 UIA 语义 | `docs/design/archive/design-round2.md` §11.4 | 可访问性 | 2 |
| 3 | 5000+ 进程时 `VisibleListSync` 最坏 O(n²) | `docs/design/archive/design-round2.md` §11.3 | 性能重构 | 1（先压测） |

建议顺序的理由：先给 5000 行/5000 端口做真实基准，确认 `VisibleListSync` 和 2Hz 端口 Diff 的 p99；键盘/UIA 独立、成本中等；兄弟顺序先加观测，不提前稳定排序。

---

## 1. 兄弟节点稳定顺序

**现状**

展平后的兄弟顺序直接跟随 `NtQuerySystemInformation(SystemProcessInformation)` 的返回顺序。同一快照内稳定，但不同快照之间内核可能改变顺序，表现为行轻微跳动。

**需要决定**

- 是否始终稳定排序，还是只在检测到顺序抖动时启用。
- 排序键用 PID、名称（不区分大小写），还是 `(名称, PID)`。
- 树形模式与平铺模式的排序是否共用同一套规则。

建议先加一个“兄弟顺序变化”的诊断计数，确认目标机上是否真的抖动；若确实抖动，树形模式默认按 `(名称, PID)` 稳定排序，平铺模式的列头排序保持不变。

**验收**

- 同一组 `ProcKey` 多次展平，顺序一致。
- 进程出生/退出只造成局部插入/删除，不引起整段重排。
- 1000 次合成快照无无谓移动；分配率不破 0.1MB/s 量级。
- 工作量：S。

---

## 2. 键盘导航与 UIA 语义

**现状**

扁平 ListView 只有默认上下键；树形已有 Left/Right 折叠/展开，但仍缺 Home/End、type-ahead 和完整的 UIA 层级语义。端口视图目前只做了列头点击排序，没有键盘排序入口。

**可选方案**

- 补 Home/End、Enter/Space、字母跳转；端口视图补 Enter 查看详情或复制。
- 为进程行补 `AutomationPeer` 或 `AutomationProperties`，让 UIA 能读到名称、层级和展开/折叠状态。
- 端口列头的自定义 `StackPanel` 目前 UIA Name 为空，需要补可访问名称或改用可聚合的 Header 模板。
- 不要求引入 WPF TreeView，继续保持扁平列表与虚拟化。

**注意事项**

不要为每一行创建重量级 Peer。Round 4 实测“Mica + 选中详情 + UIA 已附加”上限是 63.6MB，做键盘/UIA 时需要复测。

**验收**

- 纯键盘完成选择、展开/折叠、打开右键菜单。
- UIA 客户端能读到 Name、Level、ExpandCollapseState。
- 500 行 + UIA 附加仍低于 80MB。
- `scripts/test-context-menu.ps1` 和 `scripts/test-network-view.ps1` 扩展出键盘/UIA 用例。
- 工作量：M。

---

## 3. 5000+ 进程时 `VisibleListSync` 最坏 O(n²)

**现状**

`Core/TreeModel.cs` 的 `VisibleListSync.Apply` 先删除不在 desired 里的行，再对每个 desired key 从当前位置向后线性查找。完全逆序时是 O(n²)。数量差超过 32 才走 `BulkObservableCollection.ReplaceAll`；等量或近似等量的重排仍逐条 `RemoveAt`/`Insert`，每次都会触发 `CollectionChanged`。

当前 440–500 进程的真实长跑没有暴露这个问题，但 5000+ 全展开、同一帧内大范围折叠/展开时可能卡帧。端口视图的 `NetworkPortListSync` 同样需要 5000 端口基准，确认 Move 次数和字符串分配。

**需要决定**

- 先把 5000 行合成基准做出来，测 `Apply` 的 p50/p99 和 `CollectionChanged` 次数，再决定是否重构。
- 如果超预算，方案包括：key→index 字典 + 最长递增子序列做最小移动；对等量大规模重排也走一次 Reset；或引入分片集合。
- 端口视图单独记录 2Hz 下的分配速率和 UI 帧时间，确认是否需要缓存端点字符串。

**验收**

- 5000 行、随机 1%/s 增删、树/平铺切换、批量折叠下，单次 `Apply` p99 < 16ms（或先锁定一个可接受预算），无 >100ms 卡顿。
- 5000 端口、2Hz 下端口 Diff 不产生持续 `CollectionChanged`，UI 帧时间可接受。
- 分配率仍不破 0.1MB/s 量级（端口视图单独口径）。
- 自检加入 5000 行合成用例，覆盖顺序正确性与 Reset 次数。
- 工作量：M–L。

---

## 其它已知限制（暂不排期）

| 项 | 来源 | 说明 |
|---|---|---|
| 树杀只覆盖点击时快照 | `docs/design/archive/design-round3.md` §6.3 / README | 点击后新出生的后代不在本轮计划内；可考虑“再次结束树”或二次确认 |
| Toast 8 秒自动隐藏 | `docs/design/archive/design-round3.md` §6.4 | 失败详情需用户手动点开，不主动打断 |
| 搜索是 150ms 防抖 + `View.Refresh()` | `docs/design/archive/design-round3.md` §6.5 | 每次刷新遍历当前行，500 行可接受；万级进程需要增量索引 |
| 专用工作集 61→70.8MB 漂移 | `docs/design/design-refactor.md` 已知剩余 | 125 秒内仍低于 80MB，需要更长时间观察是否收敛 |
| 800ms 启动目标未达成 | `docs/design/design-refactor.md` / README | 当前中位 813ms，不为 13ms 做未经验证的裁剪 |
| 强杀时可能丢日志尾部 | `docs/design/design-refactor.md` 已知剩余 | 正常关闭和致命异常会 `Flush` |
| 发布包 137MB、无安装器、未签名 | README | 自包含运行时换冷启动；首次运行可能触发 SmartScreen |
| PPL/系统进程即使提权也可能读不到、杀不掉 | README / `docs/design/archive/design-round4.md` | 属系统保护，不承诺绕过 |
| Mica 90% 暗色罩层偏弱 | `docs/design/archive/design-round4.md` §8 | 调低 alpha 需重新做文字对比度验收 |
| UIA 附加抬高辅助功能内存 | `docs/design/archive/design-round4.md` §8 | 63.6MB 上限仍低于 80MB，做键盘/UIA 时复测 |
| 右键菜单 UI 测试会移动鼠标、覆盖剪贴板 | `docs/design/archive/design-round4.md` §8 | 测试脚本会恢复，但运行期间不要操作桌面 |
| 端口表只有 PID | `docs/design/design-network-view.md` | PID 复用期间进程名可能短暂错位；重复 UDP 四元组会被合并 |
| 端口视图 2Hz 分配未长跑 | `docs/design/design-network-view.md` | 352–450 端口已验虚拟化，需补 10 分钟长跑和 5000 端口基准 |

## 建议的下一轮切法

- 先做 3 的基准：5000 行进程 + 5000 端口，测 `VisibleListSync` / `NetworkPortListSync` p99 和 2Hz 分配。
- 基准通过后做 2（键盘/UIA），同时补进程列头和端口列头的 UIA Name。
- 1 先加观测计数，等目标机确认抖动再决定稳定排序。
