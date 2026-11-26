# 文件规模与拆分标准

目标：阻止新的“巨型文件”出现，并让存量超标文件只能缩小、不能继续变大。

## 硬上限

行数按原始文件行数计算（包含空行和注释），由 `scripts/check-file-size.ps1` 强制执行。

| 类型 | 范围 | 硬上限 |
|---|---|---:|
| 生产 C# | `src/**/*.cs`（不含 `SelfTest*.cs`） | **600 行** |
| 测试 C# | `src/**/SelfTest*.cs` | **800 行** |
| XAML | `src/**/*.xaml` | **500 行** |
| PowerShell | `scripts/**/*.ps1` | **600 行** |
| Markdown | 根目录文档与 `docs/**/*.md`（归档除外） | **600 行** |
| 工具 C# | `tools/**/*.cs` | **600 行** |

`docs/design/archive/**` 是历史记录，不纳入硬上限；归档文件不再继续扩写。

## 存量棘轮

当前超标文件登记在 `tools/file-size-baseline.txt`：

```text
src/TaskMgrLite/ViewModels/MainViewModel.cs=976
src/TaskMgrLite/Core/SelfTest.Tree.cs=831
```

规则：

1. 基线内文件不得超过登记行数；**只能变小，不能变大**。
2. 文件缩小后，应同步下调基线数字；降到硬上限以内时，直接从基线删除。
3. 新增文件不得通过加基线绕过检查。只有“已存在且难以一次性拆完”的文件才允许进入基线。
4. 新增基线项必须在 PR 说明里写清为什么不能立即拆分、下一步怎么拆。

## 什么时候必须拆

出现以下任一情况，优先拆文件，而不是继续加代码：

- 新增一个独立职责（采集、缓存、UI 编排、数据转换等）导致文件接近硬上限。
- 一个文件同时出现“数据层 + UI 层”或“两个以上可以独立测试的状态机”。
- XAML 里出现第二个独立视图、第二套列表或第二组工具栏。
- 测试文件开始按完全不同的领域堆积用例（树、端口、快照各自应独立）。
- 文件需要 `partial` 才能继续维护。`partial` 是拆分结果，不是逃避上限的手段。

## 拆分规则

- 一个文件一个主要类型；类型名与文件名一致。
- `partial` 只允许两种用途：
  1. 测试套件按领域物理拆分（`SelfTest.Tree.cs` 等）；
  2. 极强耦合、无法独立成类型的实现细节，且文件名必须是 `Type.Part.cs`。
- UI 代码：一个 `UserControl` 一个视图文件；共享样式放 `Themes/`；code-behind 只做控件事件接线，不写数据逻辑。
- 纯逻辑优先放 `Core/`，可被 `--selftest` 覆盖；UI 只做绑定、Diff 和调度。
- 不以“少几个文件”为目标；文件数量增加但职责清楚，是可接受的交换。

## 检查方式

本地：

```powershell
.\scripts\check-file-size.ps1
```

它会输出每个超标文件及当前行数，失败时退出码非 0。`scripts/check.ps1` 已把它作为第一步，CI 也会在 build 前执行。

手动查看当前最大文件：

```powershell
Get-ChildItem src\TaskMgrLite -Recurse -File -Include *.cs,*.xaml |
  Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
  ForEach-Object {
    [pscustomobject]@{
      Lines = [IO.File]::ReadAllLines($_.FullName).Count
      Path  = $_.FullName.Substring((Get-Location).Path.Length + 1)
    }
  } | Sort-Object Lines -Descending | Select-Object -First 15
```
