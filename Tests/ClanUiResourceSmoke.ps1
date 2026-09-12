#requires -Version 7.0
# Runs production resource ownership/lifetime code with simulated Unity discovery.
# Does not validate actual assets, Canvas rendering, fonts, or Unity event timing.
$ErrorActionPreference = 'Stop'
$source = Get-Content (Join-Path (Split-Path $PSScriptRoot) 'ClanUiFactory.cs') -Raw
if ($source.Contains('_selectSound') -or $source.Contains('"sfx_gui_select"')) {
    throw 'Clan button styling must not load a selection sound in addition to its click sound.'
}
if (-not $source.Contains('sfx.m_selectSfxPrefab = null;')) {
    throw 'Clan button styling must explicitly suppress the Valheim 1.0.7 pointer-selection sound.'
}
function Block([string]$declaration) {
    $start=$source.IndexOf($declaration,[StringComparison]::Ordinal)
    if($start -lt 0){throw "Missing $declaration"}
    $open=$source.IndexOf('{',$start); $depth=0
    for($i=$open;$i -lt $source.Length;$i++){
        if($source[$i] -eq '{'){$depth++}
        if($source[$i] -eq '}'){$depth--;if($depth -eq 0){return $source.Substring($start,$i-$start+1)}}
    }
    throw "Unclosed $declaration"
}
$inputStyle = Block 'internal static void ApplyInputFieldStyle('
if (-not $inputStyle.Contains('image.color = Color.white;')) {
    throw 'Valheim text-field sprites must use a white image tint instead of retaining the black construction fallback.'
}
if (-not $inputStyle.Contains('SceneManager.GetActiveScene().name == "start" ? 2f : 1f')) {
    throw 'Input-field sliced borders must preserve Jotunn-compatible start/world pixel density.'
}
$methods=(@('internal static void Initialize()', 'internal static void Dispose()',
    'internal static void Tick()',
    'private static void OnSceneLoaded(', 'private static void ReleaseResources()',
    'internal static bool PrepareResources()', 'internal static Font GetFont(',
    'private static void AcquireGameAssets()') | ForEach-Object {Block $_}) -join "`n"
$namespace='ClanUiResourceTest'+[guid]::NewGuid().ToString('N')
$harness=@"
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
namespace $namespace {
public static class UnityEngine {
    public class Object { public string name = ""; public bool Destroyed; public static void Destroy(Object value) { if(value != null) value.Destroyed=true; } }
}
public class Font : UnityEngine.Object { }
public class Material : UnityEngine.Object { }
public class Sprite : UnityEngine.Object { }
public class GameObject : UnityEngine.Object { public void SetActive(bool active) { } }
public class RectTransform : UnityEngine.Object { public GameObject gameObject=new(); }
public class ButtonSfx : UnityEngine.Object { public GameObject m_sfxPrefab; public GameObject m_selectSfxPrefab; }
public class SpriteAtlas : UnityEngine.Object {
    public readonly List<Sprite> Copies=new();
    public Sprite GetSprite(string name) { var result=new Sprite {name=name}; Copies.Add(result); return result; }
}
public static class Resources {
    public static List<UnityEngine.Object> All=new(); public static int Searches;
    public static T[] FindObjectsOfTypeAll<T>() { Searches++; return All.OfType<T>().ToArray(); }
}
public static class Time { public static float unscaledTime; }
public struct AssetID { public int Value; public AssetID(int value){Value=value;} }
public interface IAssetLoader {
    Dictionary<string,AssetID> GetAllAssetPathsMappedToAssetID();
    bool IsAvailable(AssetID id); void Load(AssetID id); void Release(AssetID id);
}
public class AssetBundleLoader : IAssetLoader {
    private bool Initialized { get; set; }
    public void Ready() { Initialized=true; }
    public readonly Dictionary<string,AssetID> Paths=new();
    public readonly List<AssetID> Loaded=new(), Released=new();
    public int Maps;
    public Dictionary<string,AssetID> GetAllAssetPathsMappedToAssetID(){Maps++;return Paths;}
    public bool IsAvailable(AssetID id)=>true;
    public void Load(AssetID id){Loaded.Add(id);}
    public void Release(AssetID id){Released.Add(id);}
}
public static class Runtime {
    private static IAssetLoader s_assetLoader;
    public static int ExtendedManifestRequests;
    public static void MakeAllAssetsLoadable(){ExtendedManifestRequests++;}
    public static void Install(IAssetLoader loader){s_assetLoader=loader;}
    public static bool HasLoader=>s_assetLoader!=null;
}
public struct Scene { }
public enum LoadSceneMode { Single, Additive }
public static class SceneManager {
    public static event Action<Scene,LoadSceneMode> sceneLoaded;
    public static int Subscribers => sceneLoaded?.GetInvocationList().Length ?? 0;
    public static void Raise(LoadSceneMode mode) => sceneLoaded?.Invoke(new Scene(),mode);
}
public static class ClanUiFactory {
    internal static event Action ResourcesAvailable;
    private static bool _initialized, _resourcesReady;
    private static float _nextResourceAttempt;
    private static Font _regularFont, _boldFont;
    private static readonly Dictionary<string,Sprite> Sprites=new(StringComparer.Ordinal);
    private static readonly Dictionary<string,Material> Materials=new(StringComparer.Ordinal);
    private static readonly List<Sprite> OwnedSprites=new();
    private static GameObject _buttonSound;
    private static RectTransform _overlay;
    private static readonly FieldInfo GameAssetLoader=typeof(Runtime).GetField("s_assetLoader",BindingFlags.Static|BindingFlags.NonPublic);
    private static readonly PropertyInfo GameAssetLoaderReady=typeof(AssetBundleLoader).GetProperty("Initialized",BindingFlags.Instance|BindingFlags.NonPublic);
    private static readonly HashSet<string> RequiredAssetNames=new(StringComparer.OrdinalIgnoreCase){"AveriaSerifLibre-Regular","AveriaSerifLibre-Bold","UIAtlas","litpanel","lithud","sfx_gui_button"};
    private static readonly List<AssetID> HeldAssets=new();
    private static IAssetLoader _assetOwner;
    internal static bool IsHeadless;
    internal static bool ResourcesReady => _resourcesReady && _regularFont != null && _boldFont != null;
    $methods
    private static int checks;
    private static void Check(bool condition,string message){ if(!condition)throw new Exception(message); checks++; }
    public static int Run(){
        Dispose(); Resources.All.Clear(); Time.unscaledTime=0; IsHeadless=true;
        Check(!PrepareResources() && Resources.Searches==0,"Headless must not search Unity resources");
        IsHeadless=false;
        Check(!PrepareResources(),"Missing fonts must defer construction");
        int failedSearches=Resources.Searches;
        Check(!PrepareResources() && Resources.Searches==failedSearches,"Missing resources retry must be throttled");
        var regular=new Font{name="AveriaSerifLibre-Regular"};
        var bold=new Font{name="AveriaSerifLibre-Bold"};
        var atlas=new SpriteAtlas{name="UIAtlas"};
        var borrowedSprite=new Sprite{name="button"};
        var material=new Material{name="lithud"};
        var click=new GameObject{name="sfx_gui_button"};
        Resources.All.AddRange(new UnityEngine.Object[]{regular,bold,atlas,borrowedSprite,material,
            new Material{name="litpanel"},new ButtonSfx{m_sfxPrefab=click}});
        int readyEvents=0; ResourcesAvailable += ()=>readyEvents++;
        Initialize(); Initialize();
        Check(SceneManager.Subscribers==1,"Initialization subscribes once");
        Check(Runtime.ExtendedManifestRequests==1 && !Runtime.HasLoader,"Register extended assets without prematurely creating the loader");
        Time.unscaledTime=1;
        Tick();
        Check(ResourcesReady && readyEvents==1,"Independent UI tick must publish readiness exactly once");
        Check(ReferenceEquals(GetFont(false),regular) && ReferenceEquals(GetFont(true),bold),"Keep regular and bold fonts distinct");
        Check(!ReferenceEquals(Sprites["button"],borrowedSprite),"Prefer game atlas over ambiguous sprite names");
        Check(OwnedSprites.Count==4 && atlas.Copies.Count==4,"Own only requested atlas copies");
        Check(ReferenceEquals(Materials["lithud"],material),"Borrow game material");
        Check(ReferenceEquals(_buttonSound,click),"Keep the click sound without a second selection sound");
        int stableSearches=Resources.Searches;
        for(int i=0;i<100;i++)if(!PrepareResources())throw new Exception("Cached resources unavailable");
        Check(Resources.Searches==stableSearches,"No repeated resource search while controls are built");
        var overlay=new RectTransform(); _overlay=overlay;
        SceneManager.Raise(LoadSceneMode.Additive);
        Check(!overlay.gameObject.Destroyed && OwnedSprites.Count==4,"Additive scene must not destroy live UI");
        SceneManager.Raise(LoadSceneMode.Single);
        Check(overlay.gameObject.Destroyed && _overlay==null,"Scene replacement clears owned overlay");
        Check(atlas.Copies.All(x=>x.Destroyed),"Scene replacement destroys atlas copies");
        Check(!borrowedSprite.Destroyed && !regular.Destroyed && !bold.Destroyed && !material.Destroyed && !click.Destroyed,
            "Never destroy borrowed game resources");
        Check(Sprites.Count==0 && Materials.Count==0 && !_resourcesReady,"Scene replacement invalidates resource cache");
        Tick();
        Check(ResourcesReady && readyEvents==2,"Next scene independently reacquires resources and republishes readiness");
        Dispose(); Dispose();
        Check(SceneManager.Subscribers==0 && OwnedSprites.Count==0,"Dispose unsubscribes and is idempotent");
        var loader=new AssetBundleLoader(); Runtime.Install(loader);
        AcquireGameAssets();
        Check(loader.Maps==0 && loader.Loaded.Count==0,"Never start an uninitialized game loader");
        loader.Paths["assets/fonts/AveriaSerifLibre-Regular.ttf"]=new AssetID(1);
        loader.Paths["assets/fonts/AveriaSerifLibre-Bold.ttf"]=new AssetID(2);
        loader.Paths["assets/gui/UIAtlas.spriteatlas"]=new AssetID(3);
        loader.Paths["assets/gui/lithud.mat"]=new AssetID(4);
        loader.Paths["other/lithud.mat"]=new AssetID(5);
        loader.Paths["assets/unused.prefab"]=new AssetID(6);
        loader.Ready(); AcquireGameAssets(); AcquireGameAssets();
        Check(loader.Maps==1 && loader.Loaded.Count==3,"Load only specific unambiguous UI assets once");
        Check(!loader.Loaded.Any(id=>id.Value>=4),"Do not load ambiguous or unrelated assets");
        ReleaseResources();
        Check(loader.Released.Count==0,"Keep asset leases through scene transitions");
        Dispose(); Dispose();
        Check(loader.Released.Count==3 && HeldAssets.Count==0 && _assetOwner==null,"Release every acquired lease exactly once");
        return checks;
    }
}
}
"@
$types=Add-Type -TypeDefinition $harness -PassThru -CompilerOptions '/nullable:annotations'
$runner=$types | Where-Object FullName -eq "$namespace.ClanUiFactory"
$checks=$runner.GetMethod('Run').Invoke($null,@())
"PASS: $checks production-linked UI resource/lifecycle checks; Unity assets and rendering require game execution."
