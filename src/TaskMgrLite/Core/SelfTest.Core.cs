using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite.Core;


/// <summary>
/// UI 状态、排序、缓存、命令/详情、假数据与通用断言工具。
/// </summary>
public static partial class SelfTest
{
    private static void UiStateRoundTrip()
    {
        string root = NewTempRoot();
        try
        {
            var state = new UiState(
                "svchost",
                ProcessViewMode.Tree,
                new[] { new ProcKey(10, 100), new ProcKey(20, 200) },
                DateTime.UtcNow,
                "Pid",
                ListSortDirection.Descending);

            string path = UiStateStore.Save(state, root);
            True(File.Exists(path), "state file written");

            UiState? loaded = UiStateStore.TryLoad(path, root);
            True(loaded.HasValue, "state loaded");
            if (!loaded.HasValue) return;

            UiState restored = loaded.Value;
            EqualText("svchost", restored.SearchText, "search restored");
            Equal((int)ProcessViewMode.Flat, (int)restored.ViewMode, "search forces flat");
            Equal(2, restored.Collapsed.Length, "collapsed count");
            True(restored.Collapsed[0].Equals(new ProcKey(10, 100)), "first key");
            EqualText("Pid", restored.SortKey!, "sort key restored");
            Equal((int)ListSortDirection.Descending, (int)restored.SortDirection, "sort direction restored");
            True(!File.Exists(path), "state file deleted after load");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateRejectsStale()
    {
        string root = NewTempRoot();
        try
        {
            var state = new UiState(
                "x",
                ProcessViewMode.Tree,
                Array.Empty<ProcKey>(),
                DateTime.UtcNow.AddMinutes(-10));

            string path = UiStateStore.Save(state, root);
            True(UiStateStore.TryLoad(path, root) is null, "stale rejected");
            True(!File.Exists(path), "stale file removed");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateRejectsBadVersion()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "bad-version.json");
            File.WriteAllText(
                path,
                "{\"version\":99,\"savedAtUtc\":\"" + DateTime.UtcNow.ToString("O") +
                "\",\"searchText\":\"x\",\"viewMode\":\"Tree\",\"collapsed\":[]}");

            True(UiStateStore.TryLoad(path, root) is null, "bad version rejected");
            True(!File.Exists(path), "bad version file removed");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateRejectsMalformedAndOversized()
    {
        string root = NewTempRoot();
        try
        {
            string malformed = Path.Combine(root, "malformed.json");
            File.WriteAllText(malformed, "{ not json");
            True(UiStateStore.TryLoad(malformed, root) is null, "malformed rejected");
            True(!File.Exists(malformed), "malformed file removed");

            string oversized = Path.Combine(root, "oversized.json");
            File.WriteAllText(oversized, new string('x', UiStateStore.MaxBytes + 1));
            True(UiStateStore.TryLoad(oversized, root) is null, "oversized rejected");
            True(!File.Exists(oversized), "oversized file removed");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateRejectsPathOutsideRoot()
    {
        string rootA = NewTempRoot();
        string rootB = NewTempRoot();
        try
        {
            var state = new UiState(
                "x",
                ProcessViewMode.Tree,
                Array.Empty<ProcKey>(),
                DateTime.UtcNow);

            string path = UiStateStore.Save(state, rootA);
            True(UiStateStore.TryLoad(path, rootB) is null, "outside path rejected");
            True(File.Exists(path), "outside path not deleted");
        }
        finally
        {
            DeleteTempRoot(rootA);
            DeleteTempRoot(rootB);
        }
    }

    private static void UiStateAcceptsLegacyJson()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "legacy.json");
            File.WriteAllText(
                path,
                "{\"version\":1,\"savedAtUtc\":\"" + DateTime.UtcNow.ToString("O") +
                "\",\"searchText\":\"\",\"viewMode\":\"Tree\",\"collapsed\":[]}");

            UiState? loaded = UiStateStore.TryLoad(path, root);
            True(loaded.HasValue, "legacy state loaded");
            if (!loaded.HasValue) return;

            True(loaded.Value.SortKey is null, "legacy has no sort key");
            Equal((int)ListSortDirection.Ascending, (int)loaded.Value.SortDirection,
                "legacy defaults to ascending");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void UiStateDropsUnknownSortKey()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "unknown-sort.json");
            File.WriteAllText(
                path,
                "{\"version\":1,\"savedAtUtc\":\"" + DateTime.UtcNow.ToString("O") +
                "\",\"searchText\":\"\",\"viewMode\":\"Flat\",\"collapsed\":[]," +
                "\"sortKey\":\"Bogus\",\"sortDirection\":\"Descending\"}");

            UiState? loaded = UiStateStore.TryLoad(path, root);
            True(loaded.HasValue, "state with unknown sort loaded");
            if (!loaded.HasValue) return;

            True(loaded.Value.SortKey is null, "unknown sort key dropped");
            Equal((int)ListSortDirection.Descending, (int)loaded.Value.SortDirection,
                "direction still parsed");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static void ElevationArguments()
    {
        string arguments = ElevationHelper.BuildArguments(
            new[] { @"C:\app\TaskMgrLite.exe", "--real", @"--restore-ui=C:\old\state.json" },
            @"C:\Temp\new state.json");

        True(arguments.Contains("--real", StringComparison.Ordinal), "flag kept");
        True(!arguments.Contains("old", StringComparison.Ordinal), "old restore removed");
        True(arguments.Contains(
                "\"--restore-ui=C:\\Temp\\new state.json\"",
                StringComparison.Ordinal),
            "new restore quoted");
    }

    private static void UiStateNormalizeForcesFlat()
    {
        UiState searched = new UiState(
            "svchost",
            ProcessViewMode.Tree,
            Array.Empty<ProcKey>(),
            DateTime.UtcNow).Normalize();

        Equal((int)ProcessViewMode.Flat, (int)searched.ViewMode, "search forces flat");

        UiState empty = new UiState(
            "",
            ProcessViewMode.Tree,
            Array.Empty<ProcKey>(),
            DateTime.UtcNow).Normalize();

        Equal((int)ProcessViewMode.Tree, (int)empty.ViewMode, "empty keeps tree");
    }

    private static void SortNextFlipsSameKey()
    {
        SortState state = SortState.Empty.Next("Pid");
        Equal((int)ListSortDirection.Descending, (int)state.Direction, "numeric first click descending");

        state = state.Next("Pid");
        Equal((int)ListSortDirection.Ascending, (int)state.Direction, "same key flips to ascending");

        state = state.Next("Pid");
        Equal((int)ListSortDirection.Descending, (int)state.Direction, "same key flips back");
    }

    private static void SortNextResetsOnNewKey()
    {
        SortState state = SortState.Empty.Next("Pid");
        Equal((int)ListSortDirection.Descending, (int)state.Direction, "numeric default descending");

        state = state.Next("Name");
        EqualText("Name", state.Key!, "new key");
        Equal((int)ListSortDirection.Ascending, (int)state.Direction, "text default ascending");
    }

    private static void SortFirstNumericDescending()
    {
        string[] numeric = { "Pid", "ParentPid", "CpuPercent", "MemoryMb", "HandleCount" };

        foreach (string key in numeric)
        {
            True(SortState.IsNumericKey(key), key);
            SortState state = SortState.Empty.Next(key);
            EqualText(key, state.Key!, key);
            Equal((int)ListSortDirection.Descending, (int)state.Direction, key);
        }
    }

    private static void SortFirstTextAscending()
    {
        True(!SortState.IsNumericKey("Name"), "name is text");
        SortState state = SortState.Empty.Next("Name");
        EqualText("Name", state.Key!, "name key");
        Equal((int)ListSortDirection.Ascending, (int)state.Direction, "text starts ascending");
    }

    private static void SortCommandLineDisabled()
    {
        True(!SortState.CanSort("CommandLine"), "command line cannot sort");
        True(SortState.NormalizeKey("CommandLine") is null, "command line dropped from restore");
        True(!SortState.Empty.Next("CommandLine").HasSort, "no sort state created");
        True(SortState.CanSort("Name"), "name can sort");
    }

    private static void SortStateModeGate()
    {
        SortState none = SortState.Empty;
        True(!none.ShouldApply(ProcessViewMode.Flat), "empty has nothing to apply");
        True(!none.IsSuspended(ProcessViewMode.Tree), "empty has nothing to suspend");

        SortState sorted = none.Next("Pid");
        True(!sorted.ShouldApply(ProcessViewMode.Tree), "tree suspends sort");
        True(sorted.IsSuspended(ProcessViewMode.Tree), "tree reports suspension");
        True(sorted.ShouldApply(ProcessViewMode.Flat), "flat applies sort");
        True(!sorted.IsSuspended(ProcessViewMode.Flat), "flat is not suspended");

        True(SortState.NormalizeKey("Pid") is not null, "known key kept");
        True(SortState.NormalizeKey("Bogus") is null, "unknown key rejected");
    }

    private static void OkCacheNeverExpires()
    {
        var cache = new CommandLineCache();
        ProcKey key = new(1, 1);

        cache.Set(key, new CommandLineResult("cmd.exe --flag", CommandLineStatus.Ok), 0);

        True(cache.TryGet(key, 600_000, out CommandLineResult result), "ok hit after 10 min");
        EqualText("cmd.exe --flag", result.Text!, "ok text");
    }

    private static void NegativeCacheExpiresAt30s()
    {
        var cache = new CommandLineCache();
        ProcKey key = new(1, 1);

        cache.Set(key, new CommandLineResult(null, CommandLineStatus.AccessDenied), 0);

        True(cache.TryGet(key, 29_999, out _), "denied before ttl");
        True(!cache.TryGet(key, 30_000, out _), "denied at ttl");
        Equal(0, cache.Count, "expired entry removed");
    }

    private static void ErrorCacheExpiresAt30s()
    {
        var cache = new CommandLineCache();
        ProcKey key = new(1, 1);

        cache.Set(key, new CommandLineResult(null, CommandLineStatus.Error), 0);

        True(cache.TryGet(key, 29_999, out _), "error before ttl");
        True(!cache.TryGet(key, 30_000, out _), "error at ttl");
        Equal(0, cache.Count, "error entry removed");
    }

    private static void TerminalCacheStatusesDoNotExpire()
    {
        var cache = new CommandLineCache();
        CommandLineStatus[] statuses =
        {
            CommandLineStatus.Exited,
            CommandLineStatus.ProcessReused,
            CommandLineStatus.Unsupported
        };

        for (int i = 0; i < statuses.Length; i++)
        {
            ProcKey key = new((uint)(i + 1), 1);
            cache.Set(key, new CommandLineResult(null, statuses[i]), 0);
            True(cache.TryGet(key, 30_000, out _), statuses[i].ToString());
        }
    }

    private static void CacheTimestampRefreshes()
    {
        var cache = new CommandLineCache();
        ProcKey key = new(1, 1);
        var denied = new CommandLineResult(null, CommandLineStatus.AccessDenied);

        cache.Set(key, denied, 0);
        cache.Set(key, denied, 20_000);

        True(cache.TryGet(key, 49_999, out _), "refreshed entry before ttl");
        True(!cache.TryGet(key, 50_000, out _), "refreshed entry at ttl");
    }

    private static void CommandLineCacheKeepsLruBound()
    {
        var cache = new CommandLineCache(capacity: 2);
        var ok = new CommandLineResult("cmd", CommandLineStatus.Ok);

        ProcKey k1 = new(1, 1);
        ProcKey k2 = new(2, 1);
        ProcKey k3 = new(3, 1);

        cache.Set(k1, ok, 0);
        cache.Set(k2, ok, 0);

        // 命中 k1，把它移到表头；之后 k2 是最久未使用
        True(cache.TryGet(k1, 0, out _), "k1 hit");
        cache.Set(k3, ok, 0);

        True(cache.TryGet(k1, 0, out _), "k1 kept");
        True(!cache.TryGet(k2, 0, out _), "k2 evicted");
        Equal(2, cache.Count, "capacity kept");
    }

    private static void LruCacheEvictsLeastRecentlyUsed()
    {
        var cache = new LruCache<int, string>(3);

        cache.Set(1, "a");
        cache.Set(2, "b");
        cache.Set(3, "c");

        True(cache.TryGet(1, out string? one) && one == "a", "cache hit a");
        cache.Set(4, "d");

        True(!cache.TryGet(2, out _), "least recently used evicted");
        True(cache.TryGet(1, out _), "recently used kept");
        True(cache.TryGet(3, out _), "third kept");
        True(cache.TryGet(4, out _), "new entry kept");
        Equal(3, cache.Count, "cache capacity");

        True(cache.Remove(3), "cache remove");
        Equal(2, cache.Count, "cache count after remove");
    }

    private static void CommandLineUnicodeParser()
    {
        const string text = "C:\\Program Files\\demo.exe --flag 中文";

        byte[] bytes = Encoding.Unicode.GetBytes(text);
        int size = 16 + bytes.Length;
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.WriteInt16(buffer, 0, (short)bytes.Length);
            Marshal.WriteInt16(buffer, 2, (short)bytes.Length);
            Marshal.WriteIntPtr(buffer, 8, IntPtr.Add(buffer, 16));
            Marshal.Copy(bytes, 0, IntPtr.Add(buffer, 16), bytes.Length);

            EqualText(text, CommandLineReader.ParseUnicodeString(buffer, size) ?? "<null>", "unicode text");

            Marshal.WriteInt16(buffer, 0, 0);
            EqualText(string.Empty, CommandLineReader.ParseUnicodeString(buffer, size) ?? "<null>", "empty text");

            Marshal.WriteInt16(buffer, 0, (short)bytes.Length);
            Marshal.WriteIntPtr(buffer, 8, IntPtr.Add(buffer, size));
            True(CommandLineReader.ParseUnicodeString(buffer, size) is null, "out of range pointer");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SearchFilterMatches()
    {
        True(ProcessFilter.Matches("chrome.exe", 1234, ""), "empty query");
        True(ProcessFilter.Matches("chrome.exe", 1234, null), "null query");
        True(ProcessFilter.Matches("chrome.exe", 1234, "CHROME"), "name case insensitive");
        True(ProcessFilter.Matches("chrome.exe", 1234, "234"), "pid substring");
        True(!ProcessFilter.Matches("chrome.exe", 1234, "firefox"), "no match");
    }

    private static void DetailsStatusAndDisplay()
    {
        var denied = new ProcessDetailsResult(
            null, ProcessInfoStatus.AccessDenied,
            null, ProcessInfoStatus.Exited);

        EqualText("（拒绝访问）", denied.PathDisplay, "path denied");
        EqualText("（进程已退出）", denied.UserDisplay, "user exited");
        True(denied.HasAccessDenied, "details access denied flag");

        var ok = new ProcessDetailsResult(
            @"C:\Windows\notepad.exe", ProcessInfoStatus.Ok,
            @"NT AUTHORITY\SYSTEM", ProcessInfoStatus.Ok);

        EqualText(@"C:\Windows\notepad.exe", ok.PathDisplay, "path ok");
        EqualText(@"NT AUTHORITY\SYSTEM", ok.UserDisplay, "user ok");
        True(!ok.HasAccessDenied, "details ok flag");
    }

    private static void DetailsErrorClassification()
    {
        Equal((int)ProcessInfoStatus.AccessDenied,
            (int)ProcessDetailsReader.ClassifyWin32Error(5), "details access denied");
        Equal((int)ProcessInfoStatus.Exited,
            (int)ProcessDetailsReader.ClassifyWin32Error(87), "details invalid parameter");
        Equal((int)ProcessInfoStatus.Exited,
            (int)ProcessDetailsReader.ClassifyWin32Error(1168), "details not found");
        Equal((int)ProcessInfoStatus.Error,
            (int)ProcessDetailsReader.ClassifyWin32Error(12345), "details unknown");
    }

    private static void FileTimeFormatting()
    {
        var utc = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        long fileTime = utc.ToFileTimeUtc();

        DateTime? local = ProcessTime.FromFileTime(fileTime);
        True(local.HasValue, "file time parsed");
        EqualText(local.GetValueOrDefault().ToString("yyyy-MM-dd HH:mm:ss"), ProcessTime.Format(fileTime), "file time format");
        True(ProcessTime.FromFileTime(0) is null, "zero file time");
    }

    private static void ExplorerSelectArguments()
    {
        EqualText(
            "/select,\"C:\\Program Files\\demo.exe\"",
            ExplorerLauncher.BuildSelectArguments(@"C:\Program Files\demo.exe"),
            "explorer select args");

        True(!ExplorerLauncher.TryOpen(null, out string error) && error.Length > 0, "null path rejected");
        True(!ExplorerLauncher.TryOpen(@"C:\__taskmgr_no_such_file__.exe", out _), "missing file rejected");
    }

    private static void RowStartTimeDisplay()
    {
        var row = new ProcessRowViewModel(
            1, 0, "demo.exe", 0, 0,
            new DateTime(2026, 11, 22, 10, 30, 45));

        EqualText("2026-11-22 10:30:45", row.StartTimeDisplay, "row start time");
    }

    private static void FakeDataNativeGuard()
    {
        True(!MainViewModel.ShouldLoadNative(useRealData: false), "fake mode must not hit native");
        True(MainViewModel.ShouldLoadNative(useRealData: true), "real mode may hit native");

        // 默认（无参数）必须是真实数据；只有显式 --fake 才进入假数据模式。
        True(App.ResolveUseRealData(Array.Empty<string>()), "no args means real data");
        True(App.ResolveUseRealData(new[] { "--stats" }), "--stats alone stays real");
        True(App.ResolveUseRealData(new[] { "--real" }), "--real stays real");
        True(App.ResolveUseRealData(new[] { "--real", "--fake" }), "--real wins over --fake");
        True(!App.ResolveUseRealData(new[] { "--fake" }), "--fake selects fake mode");
        True(!App.ResolveUseRealData(new[] { "--FAKE" }), "flag match is case-insensitive");

        var row = new ProcessRowViewModel(1000, 0, "fake.exe", 0, 1, DateTime.Now)
        {
            CommandLine = "\"C:\\Fake\\fake.exe\"",
            ImagePath = @"C:\Fake\fake.exe",
            UserName = "fake-user"
        };

        EqualText("\"C:\\Fake\\fake.exe\"", row.CommandLine, "fake command line stays local");
        EqualText(@"C:\Fake\fake.exe", row.ImagePath, "fake path stays local");
        EqualText("fake-user", row.UserName, "fake user stays local");
    }

    // ---------------- helpers ----------------

    private sealed class TestRow
    {
        public TestRow(ProcKey key) => Key = key;
        public ProcKey Key { get; }
    }

    private static string NewTempRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "TaskMgrLite-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTempRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private static void EqualText(string expected, string actual, string what)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Exception($"{what}: expected [{expected}], got [{actual}]");
    }

    private static void Equal(int expected, int actual, string what)
    {
        if (expected != actual)
            throw new Exception($"{what}: expected {expected}, got {actual}");
    }

    private static void True(bool condition, string what)
    {
        if (!condition) throw new Exception(what);
    }

    private static void Check(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Log.WriteLine($"[test] PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Log.WriteLine($"[test] FAIL  {name}: {ex.Message}");
        }
    }
}
