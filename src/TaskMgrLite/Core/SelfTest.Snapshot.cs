using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite.Core;


/// <summary>
/// 不可变快照、采集线程释放、越界解析、结束进程身份校验与报告。
/// </summary>
public static partial class SelfTest
{
    private static void KillGuardsBlockSystemAndSelf()
    {
        uint selfPid = (uint)Environment.ProcessId;

        Equal((int)KillStatus.SystemProcess,
            (int)ProcessTerminator.Kill(new ProcKey(0, 1), selfPid).Status, "idle blocked");
        Equal((int)KillStatus.SystemProcess,
            (int)ProcessTerminator.Kill(new ProcKey(4, 1), selfPid).Status, "system blocked");
        Equal((int)KillStatus.SelfProcess,
            (int)ProcessTerminator.Kill(new ProcKey(selfPid, 1), selfPid).Status, "self blocked");
    }

    private static void KillErrorClassification()
    {
        Equal((int)KillStatus.AccessDenied,
            (int)ProcessTerminator.ClassifyWin32Error(5), "access denied");
        Equal((int)KillStatus.AlreadyExited,
            (int)ProcessTerminator.ClassifyWin32Error(87), "invalid parameter");
        Equal((int)KillStatus.AlreadyExited,
            (int)ProcessTerminator.ClassifyWin32Error(1168), "not found");
        Equal((int)KillStatus.Failed,
            (int)ProcessTerminator.ClassifyWin32Error(12345), "unknown error");
    }

    private static void KillReportSummarizesFailures()
    {
        var report = new KillReport(new[]
        {
            new KillOutcome(new ProcKey(10, 1), KillStatus.Success, 0),
            new KillOutcome(new ProcKey(11, 1), KillStatus.AccessDenied, 5)
        });

        Equal(1, report.Succeeded, "report success");
        Equal(1, report.Failed, "report failure");
        True(report.HasAccessDenied, "report access denied");

        string summary = report.BuildSummary("结束进程");
        True(summary.Contains("失败 1", StringComparison.Ordinal), "summary failure count");

        string message = report.BuildMessage("结束进程");
        True(message.Contains("成功 1", StringComparison.Ordinal), "message success count");
        True(message.Contains("拒绝访问", StringComparison.Ordinal), "message reason");
        True(message.Contains("PID 11", StringComparison.Ordinal), "message pid");
    }

    private static void CollectorDisposeGuard()
    {
        bool disposed = false;

        // Simulate Join(2000) timing out: worker still alive.
        True(!ProcessCollector.SafeDispose(false, () => disposed = true), "worker alive must not dispose");
        True(!disposed, "dispose action must not run");

        // Worker exited: resources may be released once.
        True(ProcessCollector.SafeDispose(true, () => disposed = true), "worker exited may dispose");
        True(disposed, "dispose action must run");
    }

    private static void CommandLineBufferGrow()
    {
        IntPtr buffer = new(12345);
        int capacity = 123;
        bool released = false;

        // Simulate OOM in AllocHGlobal: old pointer and capacity must survive.
        bool threw = false;
        try
        {
            CommandLineReader.GrowBuffer(
                ref buffer,
                ref capacity,
                8192,
                _ => throw new OutOfMemoryException(),
                _ => released = true);
        }
        catch (OutOfMemoryException)
        {
            threw = true;
        }

        True(threw, "OOM should propagate");
        True(buffer == new IntPtr(12345), "old buffer must not change");
        Equal(123, capacity, "old capacity must not change");
        True(!released, "old buffer must not be released on failed alloc");

        // Success path: allocate new, then release old.
        IntPtr grown = new(67890);
        IntPtr releasedPointer = IntPtr.Zero;
        bool releasedAfterAlloc = false;

        CommandLineReader.GrowBuffer(
            ref buffer,
            ref capacity,
            5000,
            _ => { releasedAfterAlloc = !released; return grown; },
            pointer => releasedPointer = pointer);

        True(buffer == grown, "buffer should be replaced");
        Equal(5000, capacity, "capacity should grow");
        True(releasedAfterAlloc, "release must happen after successful alloc");
        True(releasedPointer == new IntPtr(12345), "old pointer should be released");
    }

    private static void SnapshotSlotOwnership()
    {
        int released = 0;
        ProcessSnapshot Make() => new(
            new ProcessSample[1], 0, 0, 0,
            _ => released++);

        var slot = new SnapshotSlot();
        ProcessSnapshot first = Make();
        ProcessSnapshot second = Make();

        slot.Publish(first);
        slot.Publish(second);
        Equal(1, released, "publish releases old snapshot");

        ProcessSnapshot? taken = slot.Take();
        True(ReferenceEquals(taken, second), "take returns latest");
        True(slot.Take() is null, "slot empty after take");

        taken!.Release();
        Equal(2, released, "ui releases taken snapshot");
        taken.Release();
        Equal(2, released, "release is idempotent");

        ProcessSnapshot third = Make();
        slot.Publish(third);
        slot.Discard();
        Equal(3, released, "discard releases latest");
        slot.Discard();
        Equal(3, released, "second discard is a no-op");
        True(slot.Take() is null, "discard clears slot");
    }

    private static void ParseIntoBounds()
    {
        const int header = ProcessEnumerator.HeaderSize;
        const int nameOffset = header;
        const int capacity = header + 32;

        IntPtr buffer = Marshal.AllocHGlobal(capacity);

        try
        {
            Marshal.Copy(new byte[capacity], 0, buffer, capacity);

            // One valid header with a name buffer inside the allocation.
            Marshal.WriteInt32(buffer, 0x00, 0);          // NextEntryOffset
            Marshal.WriteInt32(buffer, 0x04, 3);          // ThreadCount
            Marshal.WriteInt64(buffer, 0x08, 111);        // WorkingSetPrivate
            Marshal.WriteInt64(buffer, 0x20, 222);        // CreateTime
            Marshal.WriteInt16(buffer, 0x38, 4);          // ImageName.Length bytes
            Marshal.WriteIntPtr(buffer, 0x40, IntPtr.Add(buffer, nameOffset));
            Marshal.WriteIntPtr(buffer, 0x50, (IntPtr)123); // Pid
            Marshal.WriteIntPtr(buffer, 0x58, (IntPtr)4);   // ParentPid
            Marshal.WriteInt32(buffer, 0x60, 77);         // HandleCount
            Marshal.WriteInt64(buffer, 0x90, 333);        // WorkingSet
            Marshal.WriteInt16(buffer, nameOffset, (short)'a');
            Marshal.WriteInt16(buffer, nameOffset + 2, (short)'b');

            var entries = new RawProcessEntry[1];
            int count = ProcessEnumerator.ParseInto(buffer, capacity, ref entries);

            Equal(1, count, "valid entry count");
            Equal(123, (int)entries[0].Pid, "parsed pid");
            Equal(4, (int)entries[0].ParentPid, "parsed parent");
            Equal(222, (int)entries[0].CreateTime, "parsed create time");
            Equal(4, entries[0].NameByteLength, "parsed name length");
            True(entries[0].NamePointer == IntPtr.Add(buffer, nameOffset), "parsed name pointer");

            // Header truncated: no field may be read.
            var shortEntries = new RawProcessEntry[1];
            Equal(0, ProcessEnumerator.ParseInto(buffer, header - 1, ref shortEntries), "truncated header");

            // Name pointer outside the buffer must be zeroed.
            Marshal.WriteIntPtr(buffer, 0x40, IntPtr.Add(buffer, capacity));
            var dirtyEntries = new RawProcessEntry[1];
            Equal(1, ProcessEnumerator.ParseInto(buffer, capacity, ref dirtyEntries), "dirty name entry");
            Equal(0, dirtyEntries[0].NameByteLength, "dirty name length zeroed");
            True(dirtyEntries[0].NamePointer == IntPtr.Zero, "dirty name pointer zeroed");

            // NextEntryOffset pointing inside the fixed header must stop the walk.
            Marshal.WriteIntPtr(buffer, 0x40, IntPtr.Add(buffer, nameOffset));
            Marshal.WriteInt16(buffer, 0x38, 4);
            Marshal.WriteInt32(buffer, 0x00, 0x10);
            var badOffsetEntries = new RawProcessEntry[1];
            Equal(1, ProcessEnumerator.ParseInto(buffer, capacity, ref badOffsetEntries), "bad next offset stops after one");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
