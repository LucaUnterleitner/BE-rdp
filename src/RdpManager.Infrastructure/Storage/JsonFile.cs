using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace RdpManager.Infrastructure.Storage;

/// <summary>Atomic JSON file access: write to a temp file, then rename over the target.</summary>
public static class JsonFile
{
    public static T? Read<T>(string file, JsonTypeInfo<T> type) where T : class
    {
        if (!File.Exists(file)) return null;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.SequentialScan);
        return JsonSerializer.Deserialize(stream, type);
    }

    public static T? TryRead<T>(string file, JsonTypeInfo<T> type) where T : class
    {
        try { return Read(file, type); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    public static void Write<T>(string file, T value, JsonTypeInfo<T> type)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        WriteBytes(file, bytes);
    }

    public static void WriteBytes(string file, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = $"{file}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.WriteThrough))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        // Antivirus scanners can hold a short lock on the target; retry briefly.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tmp, file, true);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch
            {
                try { File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
        }
    }
}
