#requires -Version 7.0
<#
Compiles production panel selection, request-state clearing, view-reference cleanup,
snapshot/directory refresh, and Tick methods unchanged against small UI/RPC stubs.
The harness checks interaction decisions and dirty-state propagation; rendering,
Unity event ordering, actual tooltip timing, and full snapshot comparison are not
covered. Run alongside an in-game panel interaction check.
Run: pwsh -NoProfile -File Tests/ClanPanelInteractionSmoke.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$clanRoot = Split-Path -Parent $PSScriptRoot
$panelSource = Get-Content -LiteralPath (Join-Path $clanRoot 'ClanPanelController.cs') -Raw

function Get-SourceBlock([string] $Source, [string] $Declaration) {
    $start = $Source.IndexOf($Declaration, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing production declaration: $Declaration" }
    $open = $Source.IndexOf('{', $start)
    if ($open -lt 0) { throw "Missing production body: $Declaration" }
    $depth = 0
    for ($offset = $open; $offset -lt $Source.Length; $offset++) {
        if ($Source[$offset] -eq '{') { $depth++ }
        if ($Source[$offset] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $Source.Substring($start, $offset - $start + 1) }
        }
    }
    throw "Unterminated production body: $Declaration"
}

$productionMethods = @(
    'private static void SelectDraftEmblem(',
    'private static void ClearEditorSubmission()',
    'private static void ClearViewReferences()',
    'private static void ClearEditorError()',
    'private static bool SubmittedProfileMatches(',
    'public static void RefreshSnapshot(',
    'public static void RefreshDirectory(',
    'public static void Tick()'
) | ForEach-Object { Get-SourceBlock $panelSource $_ }
$pendingProperty = [regex]::Match(
    $panelSource, 'private static bool IsEditorSubmissionPending\s*=>[^;]+;').Value
$loadingProperty = [regex]::Match(
    $panelSource, 'private static bool IsDirectoryLoading\s*=>[^;]+;').Value
if (!$pendingProperty -or !$loadingProperty) { throw 'Missing production state property.' }
$testNamespace = 'ClanPanelSmoke_' + [guid]::NewGuid().ToString('N')

$harnessSource = @"
#nullable enable
#pragma warning disable CS0414, CS0649
using System;
using System.Reflection;

namespace $testNamespace
{
    public class GameObject
    {
        public bool activeSelf = true;
        public void SetActive(bool value) => activeSelf = value;
    }
    public class Text { public string text = ""; }
    public class InputField { }
    public class RectTransform { }
    public class Button
    {
        public bool Selected;
        public bool Interactable = true;
        public bool PendingTooltip = true;
        public int StyleChanges;
    }
    public class ScrollRect
    {
        public bool Moving = true;
        public int StopCount;
        public float verticalNormalizedPosition = 0.4f;
        public void StopMovement() { Moving = false; StopCount++; }
    }
    public static class Mathf
    {
        public static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);
    }
    public static class Time { public static float unscaledTime; }
    public sealed class Invite { public string InviteId = ""; }
    public sealed class ClanClientSnapshot
    {
        public Invite? Invite;
        public string ClanId = "clan", PrimaryClanId = "clan", GuestClanId = "";
        public string OwnApplicationClanId = "", ClanName = "Before";
        public string ClanDescription = "Description", ClanEmblemKey = "old", Status = "";
        public bool HasClan = true, IsLeader = true, CanModerate = true;
        public long ResponseRequestId;
    }
    public sealed class ClanDirectorySnapshot
    {
        public long RequestId;
        public int PresentationVersion;
    }
    public static class ClanRpc
    {
        public static bool IsDirectoryRequestPending, IsDirectoryRefreshScheduled;
        public static ClanDirectorySnapshot CurrentDirectory = new();
        public static long RequestDirectory() => 1L;
        public static void InvalidateDirectory() { CurrentDirectory = new(); }
    }
    public static class ClanLocalization
    {
        public static string Text(string key) => key;
        public static string ResolveStatus(string text) => text;
    }
    public static class ClanUiFeedback
    {
        public static int HideCount, CancelCount;
        public static void HideTooltip() => HideCount++;
        public static void CancelTooltip(Button button)
        {
            CancelCount++;
            button.PendingTooltip = false;
        }
    }

    public static class Harness
    {
        private const float MutationDirectoryRefreshDelay = 2.1f;
        private enum PanelTab { Members, Players }
        private static GameObject? _root;
        private static Button? _profileActionButton, _membersTabButton, _playersTabButton;
        private static Text? _membersTabButtonLabel;
        private static InputField? _clanSearchInput, _playerSearchInput;
        private static ScrollRect? _clanScroll, _peopleScroll, _editorEmblemScroll;
        private static RectTransform? _clanScrollContent, _peopleScrollContent;
        private static Button? _selectedEmblemButton;
        private static GameObject? _editorErrorBanner;
        private static Text? _editorErrorLabel;
        private static bool _editorOpen, _editorCreating, _clanRowsDirty, _peopleRowsDirty;
        private static bool _directoryLoadingAtLastBuild, _resetClanScrollOnNextPopulate;
        private static long _editorSubmissionRequestId;
        private static string _draftName = "", _draftDescription = "", _draftEmblemKey = "";
        private static string _editorError = "";
        private static float _emblemScrollPosition, _clanScrollPosition, _directoryRefreshAt;
        private static ClanClientSnapshot _snapshot = new();
        private static ClanDirectorySnapshot _directory = new();
        private static PanelTab _tab;
        private static int _rebuilds, _selectionClears, _headerRefreshes, _clanPopulates, _peoplePopulates;
        private static bool _inputFocused;
        private static string _rebuiltTitle = "";
        private static int _checks;
        public static bool IsOpen => _root != null && _root.activeSelf;

        $pendingProperty
        $loadingProperty
        $($productionMethods -join "`n`n")

        // Only the changed/unchanged result is needed by the production refresh flow.
        // Full production comparison and Unity view construction belong to game tests.
        private static bool HasSameSnapshotPresentation(ClanClientSnapshot a, ClanClientSnapshot b)
            => a.ClanName == b.ClanName;
        private static bool HasSameDirectoryPresentation(ClanDirectorySnapshot a, ClanDirectorySnapshot b)
            => a.PresentationVersion == b.PresentationVersion;
        private static void ClearConfirmation() { }
        private static void ScheduleDirectoryRefresh() => _directoryRefreshAt = 2.1f;
        private static void ShowEditorError(string value) => _editorError = value;
        private static void RefreshHeaderState() => _headerRefreshes++;
        private static void RefreshMembersNotificationPulse() { }
        private static void PopulateClanRows() { _clanPopulates++; _clanRowsDirty = false; }
        private static void PopulatePeopleRows() { _peoplePopulates++; _peopleRowsDirty = false; }
        private static void ClearOwnedSelection() { _selectionClears++; _inputFocused = false; }
        private static void SetPickerButtonState(Button button, bool selected, bool interactable)
        {
            button.Selected = selected;
            button.Interactable = interactable;
            button.StyleChanges++;
        }
        private static void RebuildView()
        {
            _rebuilds++;
            _rebuiltTitle = _snapshot.ClanName;
            ClanUiFeedback.HideTooltip();
            ClearOwnedSelection();
            ClearViewReferences();
            _clanRowsDirty = _peopleRowsDirty = false;
            _directoryLoadingAtLastBuild = IsDirectoryLoading;
        }
        private static void Reset()
        {
            ClearViewReferences();
            _root = new();
            _editorOpen = true;
            _editorCreating = false;
            _editorSubmissionRequestId = 0L;
            _draftName = "Typed name";
            _draftDescription = "Typed description";
            _draftEmblemKey = "old";
            _editorError = "Error";
            _editorErrorBanner = new();
            _editorErrorLabel = new() { text = "Error" };
            _selectedEmblemButton = new() { Selected = true };
            _editorEmblemScroll = new();
            _emblemScrollPosition = 0.4f;
            _clanRowsDirty = _peopleRowsDirty = _directoryLoadingAtLastBuild = false;
            _resetClanScrollOnNextPopulate = false;
            _directoryRefreshAt = float.PositiveInfinity;
            _snapshot = new();
            _directory = new();
            _tab = PanelTab.Members;
            _rebuilds = _selectionClears = _headerRefreshes = _clanPopulates = _peoplePopulates = 0;
            _inputFocused = true;
            _rebuiltTitle = "";
            ClanRpc.IsDirectoryRequestPending = ClanRpc.IsDirectoryRefreshScheduled = false;
            ClanUiFeedback.HideCount = ClanUiFeedback.CancelCount = 0;
        }
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            _checks++;
        }
        public static string Run()
        {
            Reset();
            _editorSubmissionRequestId = 42L;
            Button next = new();
            SelectDraftEmblem("new", next);
            Check(_draftEmblemKey == "old" && _editorError == "Error" && _inputFocused &&
                  _rebuilds == 0 && next.StyleChanges == 0 && ClanUiFeedback.HideCount == 0 &&
                  _editorEmblemScroll!.StopCount == 0, "Pending submission must ignore selection");
            ClearEditorSubmission();
            Check(_editorSubmissionRequestId == 0L && !IsEditorSubmissionPending,
                  "Clearing a request must also clear derived pending state");

            Reset();
            _editorSubmissionRequestId = 42L;
            RefreshSnapshot(new ClanClientSnapshot { ClanName = "Renamed", ResponseRequestId = 41L });
            SelectDraftEmblem("new", new Button());
            Check(IsEditorSubmissionPending && _clanRowsDirty && _peopleRowsDirty &&
                  _draftEmblemKey == "old" && _rebuilds == 0,
                  "Unrelated responses must not clear pending state or let dirty selection bypass it");
            ClearEditorSubmission();
            SelectDraftEmblem("new", new Button());
            Check(_rebuilds == 1 && _draftEmblemKey == "new",
                  "Dirty presentation must remain available after pending state is cleared");
            Reset();
            _editorSubmissionRequestId = 42L;
            RefreshSnapshot(new ClanClientSnapshot {
                ResponseRequestId = 42L, ClanName = "Typed name",
                ClanDescription = "Typed description", ClanEmblemKey = "old"
            });
            Check(!IsEditorSubmissionPending && !_editorOpen && _rebuilds == 1,
                  "A matching successful response must close the editor and clear derived pending state");

            Reset();
            Button old = _selectedEmblemButton!;
            ScrollRect scroll = _editorEmblemScroll!;
            GameObject banner = _editorErrorBanner!;
            Text errorLabel = _editorErrorLabel!;
            next = new();
            SelectDraftEmblem("new", next);
            Check(_rebuilds == 0 && ReferenceEquals(_editorEmblemScroll, scroll) &&
                  ReferenceEquals(_selectedEmblemButton, next) && _draftEmblemKey == "new" &&
                  !old.Selected && next.Selected && old.StyleChanges == 1 && next.StyleChanges == 1,
                  "Clean selection must update only old/new buttons and retain the view");
            Check(_editorError == "" && errorLabel.text == "" && !banner.activeSelf &&
                  !_inputFocused && _selectionClears == 1 && ClanUiFeedback.HideCount == 1 &&
                  ClanUiFeedback.CancelCount == 1 && !next.PendingTooltip,
                  "Selection must clear errors, focus, visible and pending tooltip state");
            Check(!scroll.Moving && scroll.StopCount == 1 && scroll.verticalNormalizedPosition == 0.4f &&
                  _draftName == "Typed name" && _draftDescription == "Typed description",
                  "Selection must stop inertia while preserving scroll position and input drafts");
            SelectDraftEmblem("new", next);
            Check(_rebuilds == 0 && old.StyleChanges == 1 && next.StyleChanges == 2,
                  "Selecting the same emblem must not toggle or rebuild unrelated controls");

            Reset();
            _selectedEmblemButton = null;
            _editorEmblemScroll = null;
            next = new();
            SelectDraftEmblem("", next);
            Check(_draftEmblemKey == "" && next.Selected && _rebuilds == 0,
                  "None selection must work without a previous selected button or scroll");
            foreach (float position in new[] { -0.2f, 1.2f })
            {
                Reset();
                _emblemScrollPosition = position;
                SelectDraftEmblem("new", new Button());
                Check(_editorEmblemScroll!.verticalNormalizedPosition == Mathf.Clamp01(position),
                      "Restored scroll position must keep the existing clamping policy");
            }

            foreach (bool clanDirty in new[] { true, false })
            {
                Reset();
                _clanRowsDirty = clanDirty;
                _peopleRowsDirty = !clanDirty;
                next = new();
                SelectDraftEmblem("new", next);
                Check(_rebuilds == 1 && _draftEmblemKey == "new" && _editorError == "" &&
                      !_clanRowsDirty && !_peopleRowsDirty && next.StyleChanges == 0,
                      "Either dirty pane must rebuild once with the newly selected draft");
            }
            foreach (bool loading in new[] { true, false })
            {
                Reset();
                ClanRpc.IsDirectoryRequestPending = loading;
                _directoryLoadingAtLastBuild = !loading;
                SelectDraftEmblem("new", new Button());
                Check(_rebuilds == 1 && _directoryLoadingAtLastBuild == loading,
                      "A loading transition in either direction must refresh the view");
            }

            Reset();
            RefreshSnapshot(new ClanClientSnapshot { ClanName = "Renamed" });
            Tick();
            Check(_clanRowsDirty && _peopleRowsDirty && _headerRefreshes == 0 &&
                  _clanPopulates == 0 && _peoplePopulates == 0 && _rebuilds == 0,
                  "Changed snapshot must remain dirty while the modal prevents row population");
            SelectDraftEmblem("new", new Button());
            Check(_rebuilds == 1 && _rebuiltTitle == "Renamed" && !_clanRowsDirty && !_peopleRowsDirty,
                  "Next emblem click must refresh snapshot-derived heading/background");
            Reset();
            RefreshSnapshot(new ClanClientSnapshot());
            SelectDraftEmblem("new", new Button());
            Check(_rebuilds == 0, "Unchanged snapshots must keep repeated selection cheap");

            foreach (PanelTab tab in new[] { PanelTab.Members, PanelTab.Players })
            {
                Reset();
                _tab = tab;
                RefreshDirectory(new ClanDirectorySnapshot { RequestId = 1L, PresentationVersion = 1 });
                Tick();
                Check(_clanRowsDirty && _peopleRowsDirty == (tab == PanelTab.Players) &&
                      _clanPopulates == 0 && _peoplePopulates == 0,
                      "Changed directory must preserve tab-specific dirty state under the editor");
                SelectDraftEmblem("new", new Button());
                Check(_rebuilds == 1, "Directory changes must refresh on the next emblem click");
            }
            Reset();
            _directoryLoadingAtLastBuild = true;
            RefreshDirectory(new ClanDirectorySnapshot { RequestId = 1L });
            Check(_clanRowsDirty, "Directory completion must dirty an empty loading presentation");
            Reset();
            _editorOpen = false;
            RefreshSnapshot(new ClanClientSnapshot { ClanName = "Renamed" });
            Tick();
            Check(_headerRefreshes == 1 && _clanPopulates == 1 && _peoplePopulates == 1 &&
                  !_clanRowsDirty && !_peopleRowsDirty,
                  "Outside the editor the existing header/row refresh timing must remain intact");

            Reset();
            string[] widgetFields = {
                "_profileActionButton", "_membersTabButton", "_membersTabButtonLabel", "_playersTabButton",
                "_clanSearchInput", "_playerSearchInput", "_clanScroll", "_clanScrollContent",
                "_peopleScroll", "_peopleScrollContent", "_editorEmblemScroll", "_selectedEmblemButton",
                "_editorErrorBanner", "_editorErrorLabel"
            };
            foreach (string name in widgetFields)
            {
                FieldInfo field = typeof(Harness).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
                field.SetValue(null, Activator.CreateInstance(field.FieldType));
            }
            _editorSubmissionRequestId = 99L;
            _clanRowsDirty = _peopleRowsDirty = true;
            ClearViewReferences();
            foreach (string name in widgetFields)
            {
                FieldInfo field = typeof(Harness).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
                Check(field.GetValue(null) == null, "View cleanup must release " + name);
            }
            Check(_editorOpen && IsEditorSubmissionPending && _draftName == "Typed name" &&
                  _clanRowsDirty && _peopleRowsDirty && IsOpen,
                  "View-reference cleanup must not consume logical, pending, dirty or root state");
            return "PASS: " + _checks + " production-linked panel interaction checks (UI/RPC stubs; game execution still required).";
        }
    }
}
"@

$compiledTypes = Add-Type -TypeDefinition $harnessSource -Language CSharp -PassThru
$harness = $compiledTypes | Where-Object { $_.FullName -eq "$testNamespace.Harness" }
if (!$harness) { throw 'Panel interaction harness did not compile.' }
$harness.GetMethod('Run').Invoke($null, @())
