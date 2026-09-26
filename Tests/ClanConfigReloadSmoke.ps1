#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Get-Content -LiteralPath (Join-Path $root 'Plugin.cs') -Raw
function Get-Body([string]$declaration) {
    $start = $source.IndexOf($declaration, [StringComparison]::Ordinal)
    if ($start -lt 0) { throw "Missing $declaration" }
    $open = $source.IndexOf('{', $start)
    $depth = 0
    for ($i = $open; $i -lt $source.Length; $i++) {
        if ($source[$i] -eq '{') { $depth++ }
        if ($source[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) { return $source.Substring($start, $i - $start + 1) }
        }
    }
    throw "Unterminated $declaration"
}
$start = $source.IndexOf('private FileSystemWatcher? _watcher;', [StringComparison]::Ordinal)
$end = $source.IndexOf('public enum Toggle', $start, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -lt 0) { throw 'Missing watcher fields' }
$members = $source.Substring($start, $end - $start)
foreach ($method in @('SetupWatcher', 'InvalidateConfigSnapshot', 'ReadConfigValues', 'ReloadPendingConfig', 'DisposeWatcher')) {
    $members += "`n" + (Get-Body "private void $method(")
}
$members += "`n" + (Get-Body 'private static void TryShutdown(')
if (!(Get-Body 'private void Update(').Contains('ReloadPendingConfig();')) { throw 'Missing frame pump' }
if (!(Get-Body 'private void OnDestroy(').Contains('DisposeWatcher();')) { throw 'Missing watcher cleanup' }

# Compile the actual watcher fields/methods unchanged. Filesystem events are real;
# the dispatcher models BepInEx's queued main-thread callbacks and Config is a
# controlled value/save substitute. This does not execute Unity or ServerSync.
$fixture = @'
#nullable enable
// .NET's annotated events differ from the game's unannotated .NET Framework events.
#pragma warning disable CS8622
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
public sealed class ConfigReloadSubject : IDisposable {
    private const string ConfigFileName = "test.cfg";
    private readonly string ConfigFileFullPath;
    public readonly TestConfig Config;
    private static TestLog ClanLogger = null!;
    public readonly TestLog Log = new();
    public readonly TestDispatcher Dispatcher = new();
    PRODUCTION_MEMBERS
#nullable disable
    public ConfigReloadSubject(string directory, bool watch = false) {
        Directory.CreateDirectory(directory);
        ConfigFileFullPath = Path.Combine(directory, ConfigFileName);
        Paths.ConfigPath = directory;
        ThreadingHelper.SynchronizingObject = Dispatcher;
        ClanLogger = Log;
        Config = new TestConfig(ConfigFileFullPath);
        File.WriteAllText(ConfigFileFullPath, "A");
        SetupWatcher();
        if (!watch) _watcher.EnableRaisingEvents = false;
    }
    public object Sender => _watcher;
    public string FilePath => ConfigFileFullPath;
    public bool Pending => _configReloadPending;
    public void Notify(object sender) => ReadConfigValues(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, Paths.ConfigPath, ConfigFileName));
    public void Edit(string text) { File.WriteAllText(FilePath, text); Notify(Sender); }
    public void Tick() => ReloadPendingConfig();
    // Deterministic state tests skip the quiet delay; OS integration tests use Tick.
    public void Flush() { _configReloadAfter = DateTime.MinValue; ReloadPendingConfig(); }
    public void Dispose() => DisposeWatcher();
}
public static class Paths { public static string ConfigPath; }
public static class ThreadingHelper { public static ISynchronizeInvoke SynchronizingObject; }
public sealed class SettingChangedEventArgs : EventArgs { }
public sealed class TestConfig {
    private readonly string _path;
    private string _value = "A";
    public int Reads, Saves, ReloadThread;
    public Exception NextReloadError;
    public Action BeforeRead;
    public bool SaveOnConfigSet = true;
    public event EventHandler<SettingChangedEventArgs> SettingChanged;
    public int Subscribers => SettingChanged?.GetInvocationList().Length ?? 0;
    public TestConfig(string path) { _path = path; }
    public string Value {
        get => _value;
        set {
            if (_value == value) return;
            _value = value;
            if (SaveOnConfigSet) Save();
            SettingChanged?.Invoke(this, new SettingChangedEventArgs());
        }
    }
    public void Save() { Saves++; File.WriteAllText(_path, Value); }
    public void Reload() {
        Reads++;
        ReloadThread = Thread.CurrentThread.ManagedThreadId;
        if (NextReloadError != null) {
            var error = NextReloadError; NextReloadError = null; throw error;
        }
        var beforeRead = BeforeRead; BeforeRead = null; beforeRead?.Invoke();
        Value = File.ReadAllText(_path);
    }
}
public sealed class TestLog {
    public int Infos, Errors;
    public void LogInfo(string text) { Infos++; }
    public void LogError(string text) { Errors++; }
    public void LogWarning(string text) { throw new Exception(text); }
}
public sealed class TestDispatcher : ISynchronizeInvoke {
    private readonly ConcurrentQueue<Action> _queue = new();
    public bool InvokeRequired => true;
    public IAsyncResult BeginInvoke(Delegate method, object[] args) {
        var completion = new TaskCompletionSource<object>();
        _queue.Enqueue(() => { completion.SetResult(method.DynamicInvoke(args)); });
        return completion.Task;
    }
    public object EndInvoke(IAsyncResult result) => ((Task<object>)result).GetAwaiter().GetResult();
    public object Invoke(Delegate method, object[] args) => method.DynamicInvoke(args);
    public void Drain() {
        int count = _queue.Count;
        for (int i = 0; i < count && _queue.TryDequeue(out var action); i++) action();
    }
}
public static class ConfigReloadTests {
    private static int _checks;
    private static void Check(bool result, string label) {
        if (!result) throw new Exception(label);
        _checks++;
    }
    private static void Pump(ConfigReloadSubject s, int milliseconds) {
        var time = Stopwatch.StartNew();
        while (time.ElapsedMilliseconds < milliseconds) {
            s.Dispatcher.Drain(); s.Tick(); Thread.Sleep(10);
        }
    }
    public static string Run(string directory) {
        using (var s = new ConfigReloadSubject(Path.Combine(directory, "states"))) {
            s.Edit("B"); s.Tick();
            Check(s.Config.Reads == 0 && s.Pending, "quiet period defers reload");
            s.Edit("C"); s.Flush();
            Check(s.Config.Value == "C" && s.Config.Reads == 1, "rapid edits keep latest content");
            Check(s.Config.Saves == 0 && s.Config.SaveOnConfigSet, "reload never saves, restores true");
            s.Edit("D"); s.Flush();
            Check(s.Config.Value == "D" && s.Config.Reads == 2, "second edit inside old one-second throttle is retained");
            s.Edit("D"); s.Flush(); s.Notify(s.Sender); s.Flush();
            Check(s.Config.Reads == 2 && s.Log.Infos == 2, "identical writes and duplicate events are skipped");
            s.Config.Value = "E"; s.Edit("D"); s.Flush();
            Check(s.Config.Value == "D" && s.Config.Reads == 3, "external revert after in-game save is applied");
            Check(s.Config.Saves == 1 && File.ReadAllText(s.FilePath) == "D", "normal in-game save is preserved without reload writeback");
            s.Config.SaveOnConfigSet = false;
            s.Edit("F"); s.Flush();
            Check(s.Config.Value == "F" && !s.Config.SaveOnConfigSet, "reload restores original false flag");
            s.Config.NextReloadError = new InvalidOperationException("controlled failure");
            s.Edit("G"); s.Flush();
            Check(!s.Config.SaveOnConfigSet && !s.Pending && s.Log.Errors == 1, "non-IO failure restores flag and stops");
            s.Notify(s.Sender); s.Flush();
            Check(s.Config.Value == "G", "failed reload is not marked as successfully cached");
            s.Config.SaveOnConfigSet = true;
            s.Config.NextReloadError = new IOException("controlled transient failure");
            s.Edit("H"); s.Flush();
            Check(s.Config.SaveOnConfigSet && s.Pending && s.Log.Errors == 1, "reload IO failure restores flag and schedules retry");
            s.Flush();
            Check(s.Config.Value == "H" && !s.Pending, "retry succeeds without a new event");
            s.Edit("I");
            using (var locked = File.Open(s.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                s.Flush();
                Check(s.Pending && s.Log.Errors == 1, "real file lock schedules silent retry");
            }
            s.Flush();
            Check(s.Config.Value == "I", "unlock allows retry without another file write");
            s.Edit("J");
            using (var locked = File.Open(s.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                for (int i = 0; i < 20; i++) s.Flush();
                Check(!s.Pending && s.Log.Errors == 2, "persistent IO failure has bounded retries and one error");
            }
            s.Notify(s.Sender); s.Flush();
            Check(s.Config.Value == "J", "new notification recovers after exhausted retries");
            File.Delete(s.FilePath); s.Notify(s.Sender); s.Flush();
            Check(s.Pending, "missing file during replacement retries");
            File.WriteAllText(s.FilePath, "K"); s.Flush();
            Check(s.Config.Value == "K", "replacement appears before retry");
            s.Edit("L");
            s.Config.BeforeRead = () => File.WriteAllText(s.FilePath, "M");
            int infos = s.Log.Infos;
            s.Flush();
            Check(s.Config.Value == "M" && s.Pending && s.Log.Infos == infos, "write during reload keeps snapshot invalid and schedules a stable read");
            s.Edit("L"); s.Flush();
            Check(s.Config.Value == "L" && !s.Pending, "reverting to pre-read bytes after reload race is not skipped");
            s.Edit("M"); s.Config.BeforeRead = () => File.WriteAllText(s.FilePath, "N"); s.Flush(); s.Flush();
            Check(s.Config.Value == "N" && !s.Pending, "latest racing write settles without another notification");
            s.Edit("L"); var oldSender = s.Sender; int reads = s.Config.Reads;
            Check(s.Config.Subscribers == 1, "one setting invalidation subscription");
            s.Dispose(); s.Notify(oldSender); s.Flush();
            Check(!s.Pending && s.Config.Reads == reads && s.Config.Subscribers == 0, "shutdown cancels pending work, stale callbacks and subscription");
        }
        using (var s = new ConfigReloadSubject(Path.Combine(directory, "watcher"), true)) {
            File.WriteAllText(s.FilePath, "B");
            // Reproduce the original delayed-dispatch trigger using real OS events.
            Thread.Sleep(3000);
            Check(s.Config.Reads == 0, "background watcher cannot reload on its own");
            Pump(s, 700);
            Check(s.Config.Value == "B" && s.Config.Reads == 1 && s.Config.Saves == 0, "delayed callback applies once without self-save");
            Check(s.Config.ReloadThread == Thread.CurrentThread.ManagedThreadId, "reload runs on dispatcher main thread");
            Thread.Sleep(3000); Pump(s, 700);
            Check(s.Config.Reads == 1 && s.Log.Infos == 1, "no feedback on subsequent delayed dispatch");
            File.WriteAllText(s.FilePath, "B"); Pump(s, 700);
            Check(s.Config.Reads == 1, "actual same-byte OS write is skipped");
            string replacement = s.FilePath + ".tmp";
            File.WriteAllText(replacement, "C"); File.Move(replacement, s.FilePath, true); Pump(s, 700);
            Check(s.Config.Value == "C" && s.Config.Reads == 2, "atomic rename replacement is observed");
            Check(s.Log.Errors == 0 && s.Config.Saves == 0, "OS watcher path remains error-free and read-only");
        }
        return $"PASS: {_checks} config reload checks (real filesystem/dispatcher; controlled Config, no game execution).";
    }
}
'@
Add-Type -TypeDefinition $fixture.Replace('PRODUCTION_MEMBERS', $members)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testDirectory = Join-Path $tempRoot ('ClanConfigReload-' + [guid]::NewGuid().ToString('N'))
try { [ConfigReloadTests]::Run($testDirectory) }
finally {
    $resolved = [IO.Path]::GetFullPath($testDirectory)
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^ClanConfigReload-[a-f0-9]{32}$') { throw 'Unsafe test cleanup path' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
