using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TaskMgrLite.ViewModels;

namespace TaskMgrLite.Core;


/// <summary>
/// 无依赖自检套件入口。各测试按功能拆在 SelfTest.Tree / Snapshot / Ports / Core。
/// </summary>
public static partial class SelfTest
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        _passed = 0;
        _failed = 0;

        Check("pre-order + depth", PreOrder);
        Check("collapse hides subtree", CollapseHidesSubtree);
        Check("collapse keeps the node itself", CollapseKeepsNode);
        Check("child born while parent collapsed", ChildBornWhileCollapsed);
        Check("child born while collapsed, then expand", ChildBornWhileCollapsedThenExpand);
        Check("child dies while parent collapsed", ChildDiesWhileCollapsed);
        Check("parent dies, children become roots", ParentDiesChildrenBecomeRoots);
        Check("pid 0 is never a parent", PidZeroIsNeverParent);
        Check("young parent does not adopt older child", YoungParentDoesNotAdoptOlderChild);
        Check("older parent adopts child", OlderParentAdoptsChild);
        Check("duplicate pid keeps oldest as parent", DuplicatePidKeepsOldest);
        Check("pid reuse does not inherit collapse", PidReuseDoesNotInheritCollapse);
        Check("cycle terminates and emits once", CycleTerminates);
        Check("deep chain does not overflow", DeepChain);
        Check("collapsed set pruned on death", CollapsePrunedOnDeath);
        Check("last child death releases collapse", LastChildDeathReleasesCollapse);
        Check("parent dies while collapsed", ParentDiesWhileCollapsed);
        Check("stale or leaf toggle is rejected", ToggleRejectsStaleOrLeaf);
        Check("selection moves to collapsed ancestor", SelectionMovesToCollapsedAncestor);
        Check("selection stays outside collapsed subtree", SelectionStaysOutsideCollapsedSubtree);
        Check("collapse set supports explicit expand", CollapseSetExplicit);
        Check("leaf explicit expand is rejected", LeafExplicitExpandRejected);
        Check("ui state round-trips", UiStateRoundTrip);
        Check("ui state rejects stale", UiStateRejectsStale);
        Check("ui state rejects bad version", UiStateRejectsBadVersion);
        Check("ui state rejects malformed and oversized", UiStateRejectsMalformedAndOversized);
        Check("ui state rejects path outside root", UiStateRejectsPathOutsideRoot);
        Check("ui state accepts legacy json", UiStateAcceptsLegacyJson);
        Check("ui state drops unknown sort key", UiStateDropsUnknownSortKey);
        Check("elevation args preserve flags and path", ElevationArguments);
        Check("restore forces flat with search", UiStateNormalizeForcesFlat);
        Check("sort next flips same key", SortNextFlipsSameKey);
        Check("sort next resets on new key", SortNextResetsOnNewKey);
        Check("sort state suspends in tree and resumes in flat", SortStateModeGate);
        Check("sort first numeric click is descending", SortFirstNumericDescending);
        Check("sort first text click is ascending", SortFirstTextAscending);
        Check("sort command line is disabled", SortCommandLineDisabled);
        Check("network port diff is silent when unchanged", NetworkPortDiffSilentWhenUnchanged);
        Check("network port diff updates state without collection event", NetworkPortDiffStateOnly);
        Check("network port diff inserts and removes", NetworkPortDiffInsertRemove);
        Check("network port diff moves reordered rows", NetworkPortDiffReorder);
        Check("ok cache never expires", OkCacheNeverExpires);
        Check("negative cache expires at 30s", NegativeCacheExpiresAt30s);
        Check("error cache expires at 30s", ErrorCacheExpiresAt30s);
        Check("terminal cache statuses do not expire", TerminalCacheStatusesDoNotExpire);
        Check("cache timestamp refreshes", CacheTimestampRefreshes);
        Check("command line cache keeps lru bound", CommandLineCacheKeepsLruBound);
        Check("collapsed set bounded under churn", CollapseBoundedUnderChurn);
        Check("visible list sync inserts and moves", VisibleListSyncWorks);
        Check("visible list sync random churn", VisibleListSyncRandomChurn);
        Check("flatten steady state allocates nothing", FlattenSteadyStateDoesNotAllocate);
        Check("random forest matches oracle", RandomForestMatchesOracle);
        Check("tree kill plan is post-order", TreeKillPlanIsPostOrder);
        Check("tree kill plan rejects stale roots", TreeKillPlanRejectsStaleRoots);
        Check("tree kill plan handles cycles", TreeKillPlanHandlesCycles);
        Check("kill guards block system and self", KillGuardsBlockSystemAndSelf);
        Check("kill error classification", KillErrorClassification);
        Check("kill report summarizes failures", KillReportSummarizesFailures);
        Check("lru cache evicts least recently used", LruCacheEvictsLeastRecentlyUsed);
        Check("command line unicode string parser", CommandLineUnicodeParser);
        Check("search filter matches name and pid", SearchFilterMatches);
        Check("details status and display", DetailsStatusAndDisplay);
        Check("details error classification", DetailsErrorClassification);
        Check("file time formatting", FileTimeFormatting);
        Check("explorer select arguments", ExplorerSelectArguments);
        Check("row start time display", RowStartTimeDisplay);
        Check("collector dispose waits for worker", CollectorDisposeGuard);
        Check("command line buffer grows safely", CommandLineBufferGrow);
        Check("snapshot slot owns release", SnapshotSlotOwnership);
        Check("parse into validates bounds", ParseIntoBounds);
        Check("bulk visible sync resets once", BulkVisibleSync);
        Check("fake data never uses native lazy load", FakeDataNativeGuard);

        Log.WriteLine($"[test] {_passed} passed, {_failed} failed");
        return _failed;
    }
}
