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
    private long _lastChangeTimestamp;
    private string? _lastChangePath;
    private Exception? _error;

    private readonly string? _publicationRevisionFile;

    internal BuilderSourceChangeMonitor(string folder, IReadOnlyList<string>? inputGroups = null, string? publicationRevisionFile = null)
    {
        _folder = folder;
        _inputGroups = inputGroups ?? [];
        _publicationRevisionFile = publicationRevisionFile;
        if (publicationRevisionFile is not null)
        {
            using var file = new FileStream(publicationRevisionFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write("0\n"u8);
        }
        _watcher = new(folder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Changed += (_, args) => Changed(args.FullPath);
        _watcher.Created += (_, args) => Changed(args.FullPath);
        _watcher.Deleted += (_, args) => Changed(args.FullPath);
        _watcher.Renamed += (_, args) => { Changed(args.OldFullPath); Changed(args.FullPath); };
        _watcher.Error += (_, args) => Fault(args.GetException());
        _watcher.EnableRaisingEvents = true;
    }

    internal long Revision { get { lock (_sync) return _revision; } }
    internal DateTimeOffset? LastChangeAtUtc { get { lock (_sync) return _lastChangeAtUtc; } }
    internal string? LastChangePath { get { lock (_sync) return _lastChangePath; } }

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
            watcher.Error += (_, args) => Fault(args.GetException());
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

    internal async Task WaitForRevisionAsync(long revision, CancellationToken token)
    {
        Task changed;
        lock (_sync)
        {
            ThrowIfUnavailable();
            changed = revision == _revision ? _changed.Task : Task.CompletedTask;
        }
        await changed.WaitAsync(token).ConfigureAwait(false);
        lock (_sync) ThrowIfUnavailable();
    }

    internal async Task WaitForChangeAsync(long revision, CancellationToken token)
    {
        await WaitForRevisionAsync(revision, token).ConfigureAwait(false);
        // True trailing debounce: every save/rename resets the quiet interval.
        // The subscription remains active during compilation and cancellation/drain.
        while (true)
        {
            Task changed;
            TimeSpan remaining;
            lock (_sync)
            {
                ThrowIfUnavailable();
                changed = _changed.Task;
                remaining = TimeSpan.FromMilliseconds(250) - System.Diagnostics.Stopwatch.GetElapsedTime(_lastChangeTimestamp);
                if (remaining <= TimeSpan.Zero) return; // Drain may already have consumed the quiet interval.
            }
            try { await changed.WaitAsync(remaining, token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                lock (_sync)
                {
                    ThrowIfUnavailable();
                    if (ReferenceEquals(changed, _changed.Task)) return;
                }
            }
        }
    }

    internal bool ReportIfCurrent(long revision, Action report)
    {
        lock (_sync)
        {
            ThrowIfUnavailable();
            if (_revision != revision) return false;
            report();
            return true;
        }
    }

    private void Changed(string path)
    {
        lock (_sync)
        {
            // Explicit authored inputs take precedence over the broad output-directory filter.
            // SDK-owned generated artifacts are omitted by the input capture, avoiding loops.
            if (_files.Contains(path) || _parents.Contains(path)
                || Contains(_folder, path) && !Ignored(_folder, path)
                || _roots.Any(root => Contains(root, path) && !Ignored(root, path) || Contains(path, root))) SignalChange(path);
        }
    }

    private static bool Ignored(string root, string path) => Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
        .Any(part => Outputs.Contains(part) || part.StartsWith(".sunder-builder-", StringComparison.Ordinal));

    private static bool Contains(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private void SignalChange(string? path = null)
    {
        lock (_sync)
        {
            _revision++;
            _lastChangeAtUtc = DateTimeOffset.UtcNow;
            _lastChangeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            _lastChangePath = path;
            try { WritePublicationRevision(_error is null ? _revision : -1); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _error ??= error; }
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }
    }

    private void Fault(Exception error)
    {
        lock (_sync) { _error ??= error; SignalChange(); }
    }

    private void ThrowIfUnavailable()
    {
        if (_error is not null) throw new IOException("Source change monitoring is unavailable. Retry rebuilding when the folder is accessible.", _error);
    }

    private void WritePublicationRevision(long revision)
    {
        if (_publicationRevisionFile is null) return;
        // The final MSBuild transaction takes the same exclusive file handle.
        // Never replace the file/inode while either side holds this short fence.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            FileStream file;
            try { file = new(_publicationRevisionFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (deadline.Elapsed < TimeSpan.FromSeconds(5)) { Thread.Sleep(10); continue; }
            using (file)
            {
                file.SetLength(0);
                file.Write(System.Text.Encoding.UTF8.GetBytes(revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n"));
            }
            return;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Dispose()
    {
        _watcher.Dispose();
        foreach (var watcher in _inputs.Values) watcher.Dispose();
    }
}
