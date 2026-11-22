namespace TaskMgrLite.Core;

/// <summary>
/// 采集线程与 UI 之间唯一的快照所有权槽位。
///
/// 规则：
///   - Publish 覆盖旧快照并释放旧快照；
///   - Take 把所有权交给 UI，槽位清空；
///   - Discard 只在“UI 明确不会来取”时调用（例如 BeginInvoke 失败/Dispatcher 关闭），
///     释放当前槽位里的快照。
///
/// 任何路径都不能在槽位仍持有快照时直接 snapshot.Release()，否则缓冲可能被池复用，
/// 槽位里留下一个指向已复用数据的悬空快照。
/// </summary>
public sealed class SnapshotSlot
{
    private ProcessSnapshot? _latest;

    public void Publish(ProcessSnapshot snapshot)
    {
        ProcessSnapshot? dropped = Interlocked.Exchange(ref _latest, snapshot);
        dropped?.Release();
    }

    public ProcessSnapshot? Take() => Interlocked.Exchange(ref _latest, null);

    public void Discard()
    {
        ProcessSnapshot? dropped = Interlocked.Exchange(ref _latest, null);
        dropped?.Release();
    }
}
