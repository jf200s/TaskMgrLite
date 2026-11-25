using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite.Core;


/// <summary>
/// 端口列表 Diff：无变化零事件、状态原地更新、增删与移动。
/// </summary>
public static partial class SelfTest
{
    private static void NetworkPortDiffSilentWhenUnchanged()
    {
        var target = new ObservableCollection<NetworkPortRowViewModel>();
        var byKey = new Dictionary<PortKey, NetworkPortRowViewModel>();
        var desiredKeys = new HashSet<PortKey>();
        var names = new Dictionary<uint, string> { [10] = "chrome.exe" };

        PortEntry[] desired =
        {
            PortEntryOf("TCP4", "127.0.0.1:443", "10.0.0.1:5000", "ESTABLISHED", 10, 443, 5000),
            PortEntryOf("UDP4", "0.0.0.0:5353", "—", "—", 20, 5353, 0)
        };

        int events = 0;
        target.CollectionChanged += (_, _) => events++;

        True(NetworkPortListSync.Apply(target, desired, names, byKey, desiredKeys),
            "first frame changes");
        Equal(2, target.Count, "two rows inserted");
        EqualText("chrome.exe", target[0].ProcessName, "process name resolved");
        EqualText("Unknown", target[1].ProcessName, "missing process name is unknown");

        events = 0;
        True(!NetworkPortListSync.Apply(target, desired, names, byKey, desiredKeys),
            "second frame is unchanged");
        Equal(0, events, "no CollectionChanged when data is unchanged");
    }

    private static void NetworkPortDiffStateOnly()
    {
        var target = new ObservableCollection<NetworkPortRowViewModel>();
        var byKey = new Dictionary<PortKey, NetworkPortRowViewModel>();
        var desiredKeys = new HashSet<PortKey>();
        var names = new Dictionary<uint, string> { [10] = "chrome.exe" };

        PortEntry[] listening =
        {
            PortEntryOf("TCP4", "127.0.0.1:443", "10.0.0.1:5000", "LISTEN", 10, 443, 5000)
        };
        PortEntry[] established =
        {
            PortEntryOf("TCP4", "127.0.0.1:443", "10.0.0.1:5000", "ESTABLISHED", 10, 443, 5000)
        };

        NetworkPortListSync.Apply(target, listening, names, byKey, desiredKeys);

        int events = 0;
        target.CollectionChanged += (_, _) => events++;

        True(!NetworkPortListSync.Apply(target, established, names, byKey, desiredKeys),
            "state change is not structural");
        Equal(0, events, "state update does not raise CollectionChanged");
        EqualText("ESTABLISHED", target[0].State, "state updated in place");
    }

    private static void NetworkPortDiffInsertRemove()
    {
        var target = new ObservableCollection<NetworkPortRowViewModel>();
        var byKey = new Dictionary<PortKey, NetworkPortRowViewModel>();
        var desiredKeys = new HashSet<PortKey>();
        var names = new Dictionary<uint, string>();

        PortEntry[] first =
        {
            PortEntryOf("TCP4", "0.0.0.0:80", "1.1.1.1:1000", "ESTABLISHED", 1, 80, 1000),
            PortEntryOf("TCP4", "0.0.0.0:81", "1.1.1.1:1001", "ESTABLISHED", 2, 81, 1001)
        };
        PortEntry[] second =
        {
            PortEntryOf("TCP4", "0.0.0.0:81", "1.1.1.1:1001", "ESTABLISHED", 2, 81, 1001),
            PortEntryOf("TCP4", "0.0.0.0:82", "1.1.1.1:1002", "ESTABLISHED", 3, 82, 1002)
        };

        NetworkPortListSync.Apply(target, first, names, byKey, desiredKeys);

        int events = 0;
        bool reset = false;
        target.CollectionChanged += (_, e) =>
        {
            events++;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                reset = true;
        };

        True(NetworkPortListSync.Apply(target, second, names, byKey, desiredKeys),
            "insert/remove changes structure");
        Equal(2, target.Count, "two rows after churn");
        Equal(2, (int)target[0].Pid, "surviving row kept");
        Equal(3, (int)target[1].Pid, "new row inserted");
        True(events > 0, "collection notified");
        True(!reset, "no Reset redraw");
    }

    private static void NetworkPortDiffReorder()
    {
        var target = new ObservableCollection<NetworkPortRowViewModel>();
        var byKey = new Dictionary<PortKey, NetworkPortRowViewModel>();
        var desiredKeys = new HashSet<PortKey>();
        var names = new Dictionary<uint, string>();

        PortEntry[] first =
        {
            PortEntryOf("TCP4", "0.0.0.0:80", "1.1.1.1:1000", "ESTABLISHED", 1, 80, 1000),
            PortEntryOf("TCP4", "0.0.0.0:81", "1.1.1.1:1001", "ESTABLISHED", 2, 81, 1001),
            PortEntryOf("TCP4", "0.0.0.0:82", "1.1.1.1:1002", "ESTABLISHED", 3, 82, 1002)
        };
        PortEntry[] reordered = { first[2], first[0], first[1] };

        NetworkPortListSync.Apply(target, first, names, byKey, desiredKeys);

        int moves = 0;
        bool reset = false;
        target.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Move)
                moves++;
            if (e.Action == NotifyCollectionChangedAction.Reset)
                reset = true;
        };

        True(NetworkPortListSync.Apply(target, reordered, names, byKey, desiredKeys),
            "reorder changes structure");
        Equal(3, (int)target[0].Pid, "first row moved to front");
        Equal(1, (int)target[1].Pid, "second row follows");
        Equal(2, (int)target[2].Pid, "third row follows");
        Equal(1, moves, "one Move event");
        True(!reset, "reorder never resets");
    }

    private static PortEntry PortEntryOf(
        string protocol,
        string local,
        string remote,
        string state,
        uint pid,
        ushort localPort,
        ushort remotePort) => new(
            protocol,
            local,
            remote,
            state,
            pid,
            localPort,
            remotePort);
}
