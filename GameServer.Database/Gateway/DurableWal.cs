namespace GameServer.Database.Gateway;

/// <summary>프로세스 장애 대비 append 파일. 키 하나당 JSON 파일 하나이며 성공 시 삭제한다.</summary>
internal sealed class DurableWal
{
    private readonly string _directory;

    public DurableWal(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string DirectoryPath => _directory;

    public void Write(string key, byte[] payload)
    {
        var path = Path.Combine(_directory, key + ".json");
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public void Delete(string key)
    {
        var path = Path.Combine(_directory, key + ".json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public IEnumerable<(string Key, byte[] Payload)> ReadAll()
    {
        if (!Directory.Exists(_directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            yield return (Path.GetFileNameWithoutExtension(path), File.ReadAllBytes(path));
        }
    }
}
