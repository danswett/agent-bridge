using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host.Delivery.Staging;
using Microsoft.Win32.SafeHandles;

namespace AgentBridge.App.Core;

public sealed record UpdateCoordinatorHandoff(
    UpdateCoordinatorRequest Apply, PackageDigest ImageDigest, string RuntimeTarget);

public sealed class CoordinatorHandoff
{
    public static string RuntimeTarget => (AppContext.TargetFrameworkName
        ?? throw new InvalidDataException("coordinator-runtime-unavailable."))
        + "/" + System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;

    readonly string folder, runtimeTarget;
    readonly Func<Stream, string?>? metadata;

    public CoordinatorHandoff(string folder, string runtimeTarget) : this(folder, runtimeTarget, null) { }

    internal CoordinatorHandoff(string folder, string runtimeTarget, Func<Stream, string?>? metadata)
    {
        this.folder = folder;
        this.runtimeTarget = runtimeTarget;
        this.metadata = metadata;
    }

    public Copy Prepare(UpdateCoordinatorRequest request, string sourceImage)
    {
        var root = Full(request.Root);
        var packages = Full(request.Packages);
        var packagePath = Full(request.Package);
        var sourcePath = Full(sourceImage);
        var parent = Full(folder);
        using var installPin = PinDirectory(root, out var actualRoot);
        using var stagePin = PinDirectory(parent, out var actualParent);
        if (!Within(packages, root) || !Within(packagePath, packages) || !Within(sourcePath, root)
            || File.Exists(actualParent) || Within(actualParent, actualRoot) || Within(actualRoot, actualParent)
            || !string.Equals(Path.GetFileName(sourcePath), "agent-bridge-hostw.exe", StringComparison.OrdinalIgnoreCase))
            throw Bad("handoff-path-refused: helper, package or destination is outside its allowed layout.");
        parent = actualParent;
        var stage = Full(Path.Combine(parent, Guid.NewGuid().ToString("N")));
        var extract = Full(Path.Combine(stage, "extract"));
        var imagePath = Full(Path.Combine(stage, "agent-bridge-hostw.exe"));
        var targetPath = Full(Path.Combine(stage, "target.nupkg"));
        var manifestPath = Full(Path.Combine(stage, "coordinator.json"));
        if (Directory.Exists(stage) || File.Exists(stage)) throw Bad("handoff-path-collision.");

        using var package = DownloadMark.Hold(packagePath, request.Digest)
            ?? throw new PackageMismatchException("package-custody: cached target no longer matches its verified checksum.");
        using var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var imageDigest = new PackageDigest(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(source)));
        CoordinatorImage.RequireV1(source, metadata);
        HostwBundle.Verify(source, imageDigest, runtimeTarget);
        var info = new UpdateCoordinatorHandoff(request with
        {
            Root = root, Packages = packages, Package = targetPath,
            RestartArguments = [.. request.RestartArguments]
        }, imageDigest, runtimeTarget);
        var json = JsonSerializer.SerializeToUtf8Bytes(info);
        if (json.Length > 64 * 1024) throw Bad("handoff-request-too-large.");

        PrivateFolder.Ensure(parent, "the coordinator handoff folder");
        PrivateFolder.Ensure(stage, "the unique coordinator folder");
        PrivateFolder.Ensure(extract, "the coordinator extraction folder");
        FileStream? image = null, target = null, manifest = null;
        try
        {
            CopyNew(source, imagePath);
            image = DownloadMark.Hold(imagePath, imageDigest)
                ?? throw new PackageMismatchException("helper-copy: staged helper differs from the retained installed image.");
            HostwBundle.Verify(image, imageDigest, runtimeTarget);
            CoordinatorImage.RequireV1(image, metadata);
            CopyNew(package, targetPath);
            target = DownloadMark.Hold(targetPath, request.Digest)
                ?? throw new PackageMismatchException("package-copy: staged target differs from its verified checksum.");
            PrivateFolder.WriteNew(manifestPath, json);
            manifest = File.Open(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (manifest.Length != json.Length) throw Bad("handoff-request-changed.");
            var retained = new byte[json.Length];
            manifest.ReadExactly(retained);
            if (!retained.AsSpan().SequenceEqual(json)) throw Bad("handoff-request-changed.");
            manifest.Position = 0;
            return new Copy(stage, extract, imagePath, manifestPath, image, target, manifest);
        }
        catch
        {
            manifest?.Dispose();
            target?.Dispose();
            image?.Dispose();
            throw;
        }
    }

    static void CopyNew(Stream source, string destination)
    {
        source.Position = 0;
        using var copy = PrivateFolder.CreateNew(destination);
        source.CopyTo(copy);
        copy.Flush(flushToDisk: true);
    }

    internal static bool Within(string path, string parent)
    {
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string Full(string path)
    {
        if (path is null) throw Bad("handoff-path-refused: paths must be fully qualified.");
        path = Plain(path);
        if (!Path.IsPathFullyQualified(path)) throw Bad("handoff-path-refused: paths must be fully qualified.");
        var full = Path.GetFullPath(path);
        for (var part = full; !string.IsNullOrEmpty(part); part = Path.GetDirectoryName(part))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(part); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint)
                || (part != full && !attributes.HasFlag(FileAttributes.Directory)))
                throw Bad("handoff-path-refused: reparse point or non-directory ancestor.");
        }
        using var pin = PinDirectory(full, out var canonical);
        return canonical;
    }

    static string Plain(string path) =>
        path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\\.\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[8..] : path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
                ? path[4..] : path;

    static SafeFileHandle? PinDirectory(string path, out string canonical)
    {
        canonical = path;
        if (!OperatingSystem.IsWindows()) return null;
        var ancestor = path;
        while (!Directory.Exists(ancestor))
            ancestor = Path.GetDirectoryName(ancestor) ?? throw Bad("handoff-path-unresolved.");
        var handle = OpenDirectory(ancestor, 0, (uint)FileShare.ReadWrite, 0, 3, 0x02000000, 0);
        try
        {
            if (handle.IsInvalid) throw Bad("handoff-path-unresolved.");
            var buffer = new char[32768];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) throw Bad("handoff-path-unresolved.");
            canonical = Path.GetFullPath(Path.Combine(Plain(new string(buffer, 0, (int)length)), Path.GetRelativePath(ancestor, path)));
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle OpenDirectory(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] path, uint length, uint flags);

    static InvalidDataException Bad(string reason) => new(reason);

    public sealed class Copy(string directory, string extractionDirectory, string imagePath, string manifestPath,
        FileStream image, FileStream target, FileStream manifest) : IDisposable
    {
        public string Directory { get; } = directory;
        public string ExtractionDirectory { get; } = extractionDirectory;
        public string ImagePath { get; } = imagePath;
        public string ManifestPath { get; } = manifestPath;
        public void Dispose()
        {
            manifest.Dispose();
            target.Dispose();
            image.Dispose();
        }
    }
}
