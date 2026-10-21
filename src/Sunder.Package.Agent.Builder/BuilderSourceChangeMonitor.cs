namespace Sunder.Package.Agent.Builder;

/// <summary>Change notifications shared by App recovery and the Worker-owned .NET build loop.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed class BuilderSourceChangeMonitor : IDisposable
{
    private static readonly HashSet<string> Outputs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "dist", "build", "out", "output", "artifacts", ".git", ".sunder", "node_modules", "sunder-dev", ".vs", ".idea" };
    private readonly object _sync = new();
    private readonly string _folder;
    private readonly IReadOnlyList<string> _inputGroups;
    private readonly FileSystemWatcher _watcher;
    private readonly Dictionary<string, FileSystemWatcher> _inputs = new(StringComparer.Ordinal);
    private string[] _roots = [];
    private HashSet<string> _files = new(StringComparer.Ordinal);
    private HashSet<string> _parents = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = NewSignal();
    private long _revision;
    private DateTimeOffset? _lastChangeAtUtc;
    private Exception? _error;

    internal BuilderSourceChangeMonitor(string folder, IReadOnlyList<string>? inputGroups = null)
    {
        _folder = folder;
        _inputGroups = inputGroups ?? [];
        _watcher = new(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += (_, args) => Changed(args.FullPath);
        _watcher.Created += (_, args) => Changed(args.FullPath);
        _watcher.Deleted += (_, args) => Changed(args.FullPath);
        _watcher.Renamed += (_, args) => { Changed(args.OldFullPath); Changed(args.FullPath); };
        _watcher.Error += (_, args) => { lock (_sync) { _error = args.GetException(); _changed.TrySetResult(); } };
        _watcher.EnableRaisingEvents = true;
    }

    internal long Revision { get { lock (_sync) return _revision; } }
    internal DateTimeOffset? LastChangeAtUtc { get { lock (_sync) return _lastChangeAtUtc; } }

    // True when evaluation discovers an input which had no subscription during the build.
    internal bool ObserveInputs(IReadOnlyList<string> roots, IReadOnlyList<string> files)
    {
        var parents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
            for (var directory = Path.GetDirectoryName(file); directory is not null && parents.Add(directory); directory = Path.GetDirectoryName(directory)) { }
        bool expanded;
        lock (_sync)
        {
            expanded = roots.Any(root => !CoveredByRoot(root))
                || files.Any(file => !_files.Contains(file) && !CoveredByRoot(file));
            _roots = roots.ToArray();
            _files = new(files, StringComparer.Ordinal);
            _parents = parents;
        }
        // Share known SDK/NuGet trees to avoid expensive native subscriptions for every
        // reference directory. These are observation groups, not additional change inputs:
        // Changed still filters to the actual roots/files. Missing paths watch their ancestor.
        var desired = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var root in roots.Where(root => !Contains(_folder, root))) AddDirectory(root, recursive: true);
        foreach (var file in files.Where(file => !Contains(_folder, file)))
        {
            var group = _inputGroups.FirstOrDefault(root => Contains(root, file));
            AddDirectory(group ?? Path.GetDirectoryName(file)!, recursive: group is not null);
        }
        foreach (var directory in desired.Keys.ToArray())
            if (desired.Any(parent => parent.Value && parent.Key != directory && Contains(parent.Key, directory))) desired.Remove(directory);
        foreach (var (directory, recursive) in desired)
        {
            if (_inputs.TryGetValue(directory, out var existing))
            {
                existing.IncludeSubdirectories = recursive;
                continue;
            }
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += (_, args) => Changed(args.FullPath);
            watcher.Created += (_, args) => Changed(args.FullPath);
            watcher.Deleted += (_, args) => Changed(args.FullPath);
            watcher.Renamed += (_, args) => { Changed(args.OldFullPath); Changed(args.FullPath); };
            watcher.Error += (_, args) => { lock (_sync) { _error = args.GetException(); _changed.TrySetResult(); } };
            _inputs.Add(directory, watcher);
            watcher.EnableRaisingEvents = true;
        }
        foreach (var directory in _inputs.Keys.Where(directory => !desired.ContainsKey(directory)).ToArray())
        {
            _inputs[directory].Dispose();
            _inputs.Remove(directory);
        }
        return expanded;

        bool CoveredByRoot(string path) => Contains(_folder, path) && !Ignored(_folder, path)
            || _roots.Any(root => Contains(root, path) && !Ignored(root, path));

        void AddDirectory(string directory, bool recursive)
        {
            while (!Directory.Exists(directory))
            {
                directory = Path.GetDirectoryName(directory) ?? throw new DirectoryNotFoundException("No existing directory can observe the build input.");
                recursive = true;
            }
            desired[directory] = recursive || desired.GetValueOrDefault(directory);
        }
    }

    internal async Task WaitForChangeAsync(long revision, CancellationToken token)
    {
        Task changed;
        lock (_sync)
        {
            if (_error is not null) throw new IOException("Source change monitoring is unavailable. Retry rebuilding when the folder is accessible.", _error);
            changed = revision == _revision ? _changed.Task : Task.CompletedTask;
        }
        await changed.WaitAsync(token).ConfigureAwait(false);
        // Coalesce editor save/rename bursts. There is no time-based build retry.
        await Task.Delay(250, token).ConfigureAwait(false);
        lock (_sync) if (_error is not null) throw new IOException("Source change monitoring failed.", _error);
    }

    private void Changed(string path)
    {
        lock (_sync)
        {
            // Explicit authored inputs take precedence over the broad output-directory filter.
            // SDK-owned generated artifacts are omitted by the input capture, avoiding loops.
            if (_files.Contains(path) || _parents.Contains(path)
                || Contains(_folder, path) && !Ignored(_folder, path)
                || _roots.Any(root => Contains(root, path) && !Ignored(root, path) || Contains(path, root))) SignalChange();
        }
    }

    private static bool Ignored(string root, string path) => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
        .Any(part => Outputs.Contains(part) || part.StartsWith(".sunder-builder-", StringComparison.Ordinal));

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private void SignalChange()
    {
        lock (_sync)
        {
            _revision++;
            _lastChangeAtUtc = DateTimeOffset.UtcNow;
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose()
    {
        _watcher.Dispose();
        foreach (var watcher in _inputs.Values) watcher.Dispose();
    }
}
