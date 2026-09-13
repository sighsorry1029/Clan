#requires -Version 7.0
<#
Compiles production footer-scale and borrowed Canvas-order decisions against small
substitutes and checks shortcut/dock lifecycle wiring in source. Unity transforms,
rendering, raycasts and destroyed-object null semantics still require an in-game check.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$clanRoot = Split-Path -Parent $PSScriptRoot
$source = Get-Content -LiteralPath (Join-Path $clanRoot 'ClanVanillaChatDock.cs') -Raw
$pluginSource = Get-Content -LiteralPath (Join-Path $clanRoot 'Plugin.cs') -Raw
$factorySource = Get-Content -LiteralPath (Join-Path $clanRoot 'ClanUiFactory.cs') -Raw
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

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
}

$tick = Get-SourceBlock $source 'public static void Tick()'
$unreadyReturn = $tick.IndexOf('if (_root == null)', [StringComparison]::Ordinal)
$chatRequirement = $tick.IndexOf('if (ZNet.instance == null || Chat.instance == null)', [StringComparison]::Ordinal)
$shortcutDispatch = $tick.IndexOf('ShouldToggleClanPanelFromShortcut()', [StringComparison]::Ordinal)
Assert-True ($shortcutDispatch -ge 0 -and $shortcutDispatch -lt $chatRequirement -and $shortcutDispatch -lt $unreadyReturn) 'Clan shortcut dispatch must not wait for Chat or its dock root.'
Assert-True (-not $source.Contains('_pendingClanPanelShortcut')) 'Deferred shortcut state must not mask UI readiness failures.'

$init = Get-SourceBlock $source 'public static void Init()'
$dispose = Get-SourceBlock $source 'public static void Dispose()'
$rebuild = Get-SourceBlock $source 'private static void Rebuild(bool preservePanelInteractionState)'
$pluginUpdate = Get-SourceBlock $pluginSource 'private void Update()'
$factoryTick = Get-SourceBlock $factorySource 'internal static void Tick()'
$prepareResources = Get-SourceBlock $factorySource 'internal static bool PrepareResources()'
$shortcut = Get-SourceBlock $source 'private static bool ShouldToggleClanPanelFromShortcut('
$panelTick = Get-SourceBlock $panelSource 'public static void Tick()'
$standaloneHost = Get-SourceBlock $panelSource 'private static bool TryGetStandaloneHost('
$destroyDock = Get-SourceBlock $source 'private static void DestroyRoot(bool preservePanelInteractionState = false)'
$releaseCursor = Get-SourceBlock $source 'private static bool ShouldReleaseMouseCursor()'
$overlayParent = Get-SourceBlock $source 'private static Transform GetOverlayParent('
Assert-True ($init.Contains('ClanUiFactory.ResourcesAvailable += OnUiResourcesAvailable;')) 'Chat UI must subscribe to the replacement GUI-ready event.'
Assert-True ($dispose.Contains('ClanUiFactory.ResourcesAvailable -= OnUiResourcesAvailable;')) 'Chat UI must release the GUI-ready event subscription.'
Assert-True ($rebuild.Contains('ClanUiFactory.ResourcesReady') -and -not $rebuild.Contains('ClanUiFactory.PrepareResources()')) 'Chat rebuild must consume readiness rather than initiate resource discovery.'
Assert-True ($pluginUpdate.IndexOf('ClanUiFactory.Tick();', [StringComparison]::Ordinal) -lt $pluginUpdate.IndexOf('ClanPanelController.Tick();', [StringComparison]::Ordinal) -and $pluginUpdate.IndexOf('ClanPanelController.Tick();', [StringComparison]::Ordinal) -lt $pluginUpdate.IndexOf('ClanVanillaChatDock.Tick();', [StringComparison]::Ordinal)) 'UI resources and the standalone panel must initialize before chat processing each frame.'
Assert-True ($factoryTick.Contains('PrepareResources();') -and $prepareResources.Contains('ResourcesAvailable?.Invoke();')) 'Resource readiness must be driven independently and notify the chat dock once.'
Assert-True ($panelTick.Contains('EnsureStandaloneView()') -and $standaloneHost.Contains('ClanUiFactory.GetOverlayRoot(hud)') -and $standaloneHost.Contains('ClanUiFactory.ResourcesReady')) 'Clan panel creation must be hosted by the Clan overlay after independent UI readiness.'
Assert-True (-not $rebuild.Contains('ClanPanelController.Build(') -and -not $destroyDock.Contains('ClanPanelController.DestroyView(') -and -not $tick.Contains('ClanPanelController.Tick();') -and -not $source.Contains('ClanPanelController.RefreshPosition(')) 'Chat dock creation, destruction, positioning, and updates must not own the standalone Clan panel.'
Assert-True ($shortcut.Contains('ClanPlugin.ClanPanelShortcut.Value.IsKeyDown()') -and $shortcut.Contains('input == null || !input.isFocused') -and $shortcut.Contains('chat == null || !HasPendingChatRefocus(chat)')) 'The Clan shortcut must preserve focus guards without requiring Chat objects.'
Assert-True ($releaseCursor.IndexOf('ClanPanelController.CapturesGameplayInput', [StringComparison]::Ordinal) -lt $releaseCursor.IndexOf('if (!SupportsCurrentChatUi)', [StringComparison]::Ordinal)) 'An open standalone panel must release the cursor before chat compatibility checks.'
Assert-True ($overlayParent.Contains('Hud.instance') -and $overlayParent.Contains('ClanUiFactory.GetOverlayRoot(overlayContext)')) 'Chat and standalone panel must resolve the same HUD-owned overlay so chat attachment cannot destroy an open panel.'
Assert-True ($panelSource.Contains('PanelWidth * LegacyPanelTransformScale') -and $panelSource.Contains('PanelHeight * LegacyPanelTransformScale') -and $panelSource.Contains('PreferredPanelScreenWidth / unscaledWidth') -and $panelSource.Contains('PreferredPanelScreenHeight / unscaledHeight') -and -not $panelSource.Contains('PreferredPanelScale')) 'Standalone panel scale must target the pre-removal 2x design size in screen coordinates instead of multiplying the HUD canvas scale.'
Assert-True ($pluginSource.Contains('Input.GetKeyDown(shortcut.MainKey)')) 'Configured shortcuts must retain the previously working Unity input path.'
Assert-True (-not $pluginSource.Contains('ZInput.GetKeyDown(shortcut.MainKey')) 'The unrelated ZInput shortcut experiment must not remain.'

$refresh = Get-SourceBlock $source 'private static void RefreshDockPositions(TMP_InputField input)'
$tickScaleRefresh = $tick.IndexOf('RefreshChatPanelTarget(input.GetComponent<RectTransform>());', [StringComparison]::Ordinal)
$inputMissingReturn = $tick.IndexOf('if (input == null)', [StringComparison]::Ordinal)
Assert-True ($tickScaleRefresh -gt $inputMissingReturn) 'Chat scale state must refresh even while the chat input itself is closed.'

$refreshScaleTarget = Get-SourceBlock $source 'private static void RefreshChatScaleTarget(RectTransform? target, bool canResize)'
$inventoryGuard = $refreshScaleTarget.IndexOf('if (InventoryGui.IsVisible())', [StringComparison]::Ordinal)
$configuredScaleApply = $refreshScaleTarget.IndexOf('SetChatScale(ClanPlugin.ClanChatWindowScale.Value);', [StringComparison]::Ordinal)
Assert-True ($inventoryGuard -ge 0 -and $configuredScaleApply -gt $inventoryGuard -and $refreshScaleTarget.Contains('SetChatScale(MinimumChatScale);')) 'Inventory and crafting UI must temporarily restore vanilla chat scale before the configured scale is considered.'

$fullScale = $refresh.IndexOf('ApplyClanDockScale(dockScale);', [StringComparison]::Ordinal)
$chatBounds = $refresh.IndexOf('Rect chatBounds =', $fullScale, [StringComparison]::Ordinal)
$reserveFooter = $refresh.IndexOf('ReserveFooterDockSpace(inputRect, ref inputBounds, ref chatBounds);', $chatBounds, [StringComparison]::Ordinal)
$footerScale = $refresh.IndexOf('ApplyFooterDockScale(CalculateFooterDockScale(dockScale, chatBounds));', $reserveFooter, [StringComparison]::Ordinal)
Assert-True ($fullScale -ge 0 -and $chatBounds -gt $fullScale -and $reserveFooter -gt $chatBounds -and $footerScale -gt $reserveFooter) 'Footer fitting must reserve space below the scaled chat before applying its fallback scale.'

$applyFooter = Get-SourceBlock $source 'private static void ApplyFooterDockScale(float factor)'
foreach ($dock in @('_chatViewDockRect', '_channelDockRect', '_commandDockRect')) {
    Assert-True ($applyFooter.Contains($dock)) "Footer fitting is missing $dock."
}
Assert-True (-not $applyFooter.Contains('_emojiTrayRect') -and -not $applyFooter.Contains('_clanButtonDockRect')) 'Footer fitting must not shrink the emoji tray or Clan rail button.'

$fitMethod = Get-SourceBlock $source 'private static float FitFooterDockScale('
$namespace = 'ClanChatDockSmoke_' + [Guid]::NewGuid().ToString('N')
$harness = @"
using System;
namespace $namespace {
    internal static class Mathf {
        internal static float Min(float a, float b) => Math.Min(a, b);
        internal static float Max(float a, float b) => Math.Max(a, b);
    }
    internal static class Harness {
        private const float MinimumFooterDockScale = 0.05f;
        private static bool IsFinitePositive(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        $fitMethod
        public static float Fit(float desired, float available, float height) => FitFooterDockScale(desired, available, height);
    }
}
"@
Add-Type -TypeDefinition $harness -Language CSharp
$type = [AppDomain]::CurrentDomain.GetAssemblies() |
    ForEach-Object { $_.GetType("$namespace.Harness", $false) } |
    Where-Object { $null -ne $_ } |
    Select-Object -First 1
$fit = $type.GetMethod('Fit', [Reflection.BindingFlags]'Static,Public')
Assert-True ([Math]::Abs([float]$fit.Invoke($null, @([float]2, [float]64, [float]40)) - 1.6) -lt 0.0001) 'A 2x footer must fit to the available 1.6x lower margin.'
Assert-True ([Math]::Abs([float]$fit.Invoke($null, @([float]1.6, [float]80, [float]40)) - 1.6) -lt 0.0001) 'A footer that already fits must retain its requested scale.'
Assert-True ([Math]::Abs([float]$fit.Invoke($null, @([float]2, [float]0, [float]40)) - 0.05) -lt 0.0001) 'A footer with no lower margin must remain finite.'

$refreshTarget = Get-SourceBlock $source 'private static void RefreshChatPanelTarget('
Assert-True ($refreshTarget.Contains('RefreshChatCanvasOrder();') -and $rebuild.Contains('RefreshChatPanelTarget(inputRect);')) 'Initial attachment and inactive-chat updates must both refresh the borrowed chat Canvas order.'
Assert-True ($destroyDock.Contains('ReleaseChatCanvasOrder();') -and $dispose.Contains('DestroyRoot();')) 'Dock teardown and plugin disposal must restore the borrowed chat Canvas order.'

$canvasMethods = @(
    'private static void RefreshChatCanvasOrder()',
    'private static void ReleaseChatCanvasOrder()'
) | ForEach-Object { Get-SourceBlock $source $_ }
$canvasFields = [regex]::Matches($source, '(?m)^    private static [^\r\n]+ (?:_chatSortingWindow|_inventorySortingRoot|_chatSortingCanvas|_inventorySortingCanvas|_originalChatSortingLayer|_originalChatSortingOrder|_appliedChatSortingOrder);\r?$') |
    ForEach-Object { $_.Value }
$canvasNamespace = 'ClanChatCanvasSmoke_' + [Guid]::NewGuid().ToString('N')
Add-Type -Language CSharp -TypeDefinition @"
#nullable enable
using System;
namespace $canvasNamespace {
    public class Canvas {
        public bool overrideSorting = true;
        public int sortingLayerID;
        private int order;
        public int Writes;
        public int sortingOrder { get => order; set { order = value; Writes++; } }
    }
    public class Transform {
        public Canvas? OwnCanvas, ParentCanvas;
        public bool Active;
        public static int Searches;
        public T? GetComponent<T>() where T : class { Searches++; return OwnCanvas as T; }
        public T? GetComponentInParent<T>(bool includeInactive) where T : class {
            Searches++;
            return Active || includeInactive ? (OwnCanvas ?? ParentCanvas) as T : null;
        }
    }
    public class RectTransform : Transform { }
    public class Terminal { public RectTransform? m_chatWindow; }
    public class Chat : Terminal { public static Chat? instance; }
    public class InventoryGui { public static InventoryGui? instance; public Transform? m_inventoryRoot; }
    public static class Harness {
        $($canvasFields -join "`n")
        $($canvasMethods -join "`n")
        private static int checks;
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
        public static int Run() {
            RefreshChatCanvasOrder();
            Check(Transform.Searches == 0, "Missing UI must not perform component searches");
            var chat = new Canvas { sortingOrder = 900 };
            var inventory = new Canvas { sortingOrder = 600 };
            var window = new RectTransform { OwnCanvas = chat };
            var inventoryRoot = new Transform { ParentCanvas = inventory };
            Chat.instance = new Chat { m_chatWindow = window };
            InventoryGui.instance = new InventoryGui { m_inventoryRoot = inventoryRoot };
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 599 && inventory.sortingOrder == 600, "Inactive chat must be below crafting before either panel opens");
            int searches = Transform.Searches, writes = chat.Writes;
            for (int i = 0; i < 100; i++) RefreshChatCanvasOrder();
            Check(Transform.Searches == searches && chat.Writes == writes, "Stable frames must not search components or rewrite Canvas order");
            window.Active = inventoryRoot.Active = true;
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 599 && Transform.Searches == searches, "Opening chat or inventory must retain the prepared order");
            inventory.sortingOrder = 450;
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 449, "Use live inventory order instead of a hard-coded 599");
            var nextChat = new Canvas { sortingOrder = 950 };
            Chat.instance = new Chat { m_chatWindow = new RectTransform { OwnCanvas = nextChat } };
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 900 && nextChat.sortingOrder == 449, "Window replacement must restore the old Canvas and adjust the new one");
            var nextInventory = new Canvas { sortingOrder = 700 };
            InventoryGui.instance = new InventoryGui { m_inventoryRoot = new Transform { ParentCanvas = nextInventory } };
            RefreshChatCanvasOrder();
            Check(nextChat.sortingOrder == 699, "Inventory replacement must refresh its cached Canvas");
            InventoryGui.instance = null;
            RefreshChatCanvasOrder();
            Check(nextChat.sortingOrder == 950, "Losing inventory UI must restore chat order");
            Chat.instance.m_chatWindow = window;
            InventoryGui.instance = new InventoryGui { m_inventoryRoot = inventoryRoot };
            inventory.sortingOrder = 600;
            RefreshChatCanvasOrder();
            ReleaseChatCanvasOrder();
            Check(chat.sortingOrder == 900, "Teardown must restore the original order");
            chat.sortingOrder = 200;
            writes = chat.Writes;
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 200 && chat.Writes == writes, "Already-lower chat must not be raised");
            ReleaseChatCanvasOrder();
            chat.sortingOrder = 900;
            RefreshChatCanvasOrder();
            chat.sortingOrder = 850;
            RefreshChatCanvasOrder();
            ReleaseChatCanvasOrder();
            Check(chat.sortingOrder == 850, "Do not overwrite another mod's later order change");
            chat.sortingOrder = 900;
            RefreshChatCanvasOrder();
            chat.sortingLayerID = 10;
            RefreshChatCanvasOrder();
            ReleaseChatCanvasOrder();
            Check(chat.sortingOrder == 599 && chat.sortingLayerID == 10, "Do not undo another mod's later layer change");
            chat.sortingOrder = 900;
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 900, "Different sorting layers must be left alone");
            ReleaseChatCanvasOrder();
            chat.sortingLayerID = 0;
            chat.overrideSorting = false;
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 900 && !chat.overrideSorting, "Do not enable independent sorting on a shared hierarchy");
            ReleaseChatCanvasOrder();
            chat.overrideSorting = true;
            inventoryRoot.ParentCanvas = chat;
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 900, "A Canvas shared by chat and inventory must not be changed");
            ReleaseChatCanvasOrder();
            inventoryRoot.ParentCanvas = inventory;
            window.OwnCanvas = null;
            window.ParentCanvas = chat;
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 900, "Chat must not fall back to an unrelated parent Canvas");
            ReleaseChatCanvasOrder();
            window.OwnCanvas = chat;
            inventory.sortingOrder = short.MinValue;
            RefreshChatCanvasOrder();
            Check(chat.sortingOrder == 900, "Canvas order must not wrap beyond Unity's signed 16-bit range");
            ReleaseChatCanvasOrder();
            return checks;
        }
    }
}
"@
$canvasType = ([System.Management.Automation.PSTypeName]"$canvasNamespace.Harness").Type
$canvasChecks = $canvasType.GetMethod('Run').Invoke($null, @())
Write-Host "PASS: chat footer scaling, independent UI-readiness shortcuts and $canvasChecks borrowed Canvas-order checks passed."
