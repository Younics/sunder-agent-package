namespace Sunder.Package.Agent.TestSupport;

public static class TestSqliteFiles
{
    // Physical-erasure assertions inspect bytes while SQLite can retain a writable
    // handle. File.ReadAllBytes denies that sharing on Windows.
    public static byte[] ReadAllBytes(string path)
    {
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        source.CopyTo(bytes);
        return bytes.ToArray();
    }
}
