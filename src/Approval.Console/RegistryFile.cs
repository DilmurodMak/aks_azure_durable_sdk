using System.Text.Json;

namespace Approval.Console;

internal static class RegistryFile
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<T> ReadAsync<T>(string path, Func<T> empty, CancellationToken cancellation) where T : class
    {
        if (!File.Exists(path)) return empty();
        await using FileStream file = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(file, Json, cancellation)
            ?? throw new InvalidDataException("Registry is empty or invalid.");
    }

    public static async Task WriteAsync<T>(string path, T registry, CancellationToken cancellation)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".registry-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, registry, Json, cancellation);
                await file.FlushAsync(cancellation);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
