using System.Collections.Concurrent;
using System.IO.Hashing;
using Microsoft.Win32.SafeHandles;
using SysStateTracer.Core.Interop;
using SysStateTracer.Core.Model;

namespace SysStateTracer.Core.Scanning;

/// <summary>
/// Reads an item's permissions (owner and DACL) and turns them into the recorded form
/// (<see cref="Permissions.Normalize"/>). Most items share a handful of descriptors, so the text is cached by
/// the descriptor's bytes and each distinct descriptor is parsed once. Thread-safe; one per scan.
/// </summary>
internal sealed class SecurityReader
{
    private const uint Requested = NativeMethods.OwnerSecurityInformation | NativeMethods.DaclSecurityInformation;
    private const int InitialBuffer = 1024;

    private readonly ConcurrentDictionary<(ulong Hash, int Length), string> _texts = new();

    [ThreadStatic]
    private static byte[]? t_buffer;

    /// <summary>Distinct descriptors seen so far.</summary>
    public int Distinct => _texts.Count;

    /// <summary>The key's permissions, or null when they cannot be read (the handle lacks READ_CONTROL).</summary>
    public unsafe string? Read(SafeRegKeyHandle key)
    {
        var buffer = Buffer(InitialBuffer);
        while (true)
        {
            var length = (uint)buffer.Length;
            int error;
            fixed (byte* p = buffer)
            {
                error = NativeMethods.RegGetKeySecurity(key, Requested, p, ref length);
            }

            if (error == 0)
            {
                return Text(buffer.AsSpan(0, (int)length));
            }

            if (error != Win32Errors.InsufficientBuffer || length <= buffer.Length)
            {
                return null;
            }

            buffer = Buffer((int)length);
        }
    }

    /// <summary>The permissions of an open file or folder (opened with READ_CONTROL), or null.</summary>
    public unsafe string? Read(SafeFileHandle file)
    {
        var buffer = Buffer(InitialBuffer);
        while (true)
        {
            uint needed;
            fixed (byte* p = buffer)
            {
                if (NativeMethods.GetKernelObjectSecurity(file, Requested, p, (uint)buffer.Length, out needed))
                {
                    return Text(buffer.AsSpan(0, (int)Math.Min(NativeMethods.GetSecurityDescriptorLength(p), (uint)buffer.Length)));
                }
            }

            if (needed <= buffer.Length)
            {
                return null;
            }

            buffer = Buffer((int)needed);
        }
    }

    /// <summary>The permissions of a file or folder by path (opened without following links), or null.</summary>
    public string? ReadPath(string path)
    {
        using var handle = Fs.FileIdentity.OpenForSecurity(path);
        return handle.IsInvalid ? null : Read(handle);
    }

    /// <summary>The permissions of the file with reference number <paramref name="fileId"/> on the hint's volume, or null.</summary>
    public string? ReadById(SafeFileHandle volumeHint, long fileId)
    {
        using var handle = Fs.FileIdentity.OpenForSecurity(volumeHint, fileId);
        return handle.IsInvalid ? null : Read(handle);
    }

    /// <summary>The recorded form of a self-relative descriptor; null when it cannot be parsed.</summary>
    public string? Text(ReadOnlySpan<byte> descriptor)
    {
        var key = (XxHash3.HashToUInt64(descriptor), descriptor.Length);
        if (_texts.TryGetValue(key, out var text))
        {
            return text;
        }

        try
        {
            text = Permissions.Normalize(descriptor);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // One descriptor Windows wrote but .NET cannot convert must not stop the snapshot.
            return null;
        }

        return _texts.GetOrAdd(key, text);
    }

    private static byte[] Buffer(int size)
    {
        var buffer = t_buffer;
        if (buffer is null || buffer.Length < size)
        {
            buffer = new byte[Math.Max(size, InitialBuffer)];
            t_buffer = buffer;
        }

        return buffer;
    }
}
