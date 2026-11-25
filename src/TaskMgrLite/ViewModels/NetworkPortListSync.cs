using System.Collections.ObjectModel;
using TaskMgrLite.Core;

namespace TaskMgrLite.ViewModels;

/// <summary>
/// 把一帧端口快照 Diff 到现有 ObservableCollection。
///
/// 规则：
///   - Key = 协议 + 本地端点 + 远程端点 + PID；
///   - 已存在且位置正确：只更新 State / ProcessName，不触发 CollectionChanged；
///   - 新增、删除、换位：只对必要的行发 Insert/Remove/Move；
///   - 数据完全没变：这里不调用任何集合修改方法，CollectionChanged 次数为 0。
///
/// 已知边界：如果系统返回两条 Key 完全相同的行（例如 SO_REUSEADDR 下的
/// 重复 UDP 绑定），它们会被当作同一行；当前端口视图也不区分这种情况。
/// </summary>
internal static class NetworkPortListSync
{
    /// <returns>是否发生了结构性变化（增/删/移动）。仅属性更新返回 false。</returns>
    public static bool Apply(
        ObservableCollection<NetworkPortRowViewModel> target,
        IReadOnlyList<PortEntry> desired,
        IReadOnlyDictionary<uint, string> processNames,
        Dictionary<PortKey, NetworkPortRowViewModel> byKey,
        HashSet<PortKey> desiredKeys)
    {
        bool changed = false;

        // 1. 目标 key 集合，用来一次性删掉已经消失的端口。
        desiredKeys.Clear();
        for (int i = 0; i < desired.Count; i++)
            desiredKeys.Add(desired[i].Key);

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (desiredKeys.Contains(target[i].Key))
                continue;

            target.RemoveAt(i);
            changed = true;
        }

        // 2. 现有行索引。Key 理论上唯一；重复时保留最后一个，见类注释。
        byKey.Clear();
        for (int i = 0; i < target.Count; i++)
            byKey[target[i].Key] = target[i];

        // 3. 按期望顺序就位。命中同一位置时零集合事件。
        int position = 0;

        for (int i = 0; i < desired.Count; i++)
        {
            PortEntry entry = desired[i];
            PortKey key = entry.Key;
            string name = ResolveName(processNames, entry.Pid);

            if (position < target.Count && target[position].Key.Equals(key))
            {
                target[position].Update(in entry, name);
                position++;
                continue;
            }

            if (byKey.TryGetValue(key, out NetworkPortRowViewModel? existing) &&
                existing is not null)
            {
                int existingIndex = target.IndexOf(existing);
                if (existingIndex > position)
                {
                    target.Move(existingIndex, position);
                    existing.Update(in entry, name);
                    changed = true;
                    position++;
                    continue;
                }
            }

            var inserted = new NetworkPortRowViewModel(in entry, name);
            target.Insert(position, inserted);
            byKey[key] = inserted;
            changed = true;
            position++;
        }

        // 4. 期望序列已排完，尾部残留（包括重复 key 产生的多余行）全部删除。
        while (target.Count > desired.Count)
        {
            target.RemoveAt(target.Count - 1);
            changed = true;
        }

        return changed;
    }

    private static string ResolveName(
        IReadOnlyDictionary<uint, string> processNames,
        uint pid)
    {
        if (processNames.TryGetValue(pid, out string? name) &&
            !string.IsNullOrEmpty(name))
        {
            return name;
        }

        return pid is 0 or 4 ? "System/Unknown" : "Unknown";
    }
}
