## 变更

<!-- 说明改了什么、为什么改。 -->

## 验证

- [ ] `dotnet build src/TaskMgrLite/TaskMgrLite.csproj -c Release`：0 warning / 0 error
- [ ] `--selftest` 全绿
- [ ] 相关 diag / UI 回归已跑
- [ ] 涉及采集或渲染热路径时，分配/启动/内存指标未回退

## 风险与回退

<!-- 已知取舍、未覆盖场景、如何回退。 -->
