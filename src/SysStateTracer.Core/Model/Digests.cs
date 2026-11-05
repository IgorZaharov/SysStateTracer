using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;

namespace SysStateTracer.Core.Model;

/// <summary>Helpers shared by the subtree-digest computations.</summary>
internal static class Digests
{
    /// <summary>Appends a name prefixed with its length, so "ab"+"c" and "a"+"bc" hash differently.</summary>
    public static void AppendName(XxHash3 hasher, ReadOnlySpan<char> name)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, name.Length);
        hasher.Append(length);
        hasher.Append(MemoryMarshal.AsBytes(name));
    }

    /// <summary>
    /// Subtree digests of the permissions alone (names and data are in the main digest): an entry's own
    /// permission text (or a marker for "not read"), then its children's permission digests.
    /// </summary>
    public static ulong[] Security(int[] descendants, int[] security, NameTable names)
    {
        var n = descendants.Length;
        var digest = new ulong[n];
        var hasher = new XxHash3();
        Span<byte> buffer = stackalloc byte[8];
        for (var i = n - 1; i >= 0; i--)
        {
            hasher.Reset();
            if (security[i] >= 0)
            {
                AppendName(hasher, names[security[i]]);
            }
            else
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer, -1);
                hasher.Append(buffer[..4]);
            }

            var end = i + descendants[i];
            for (var c = i + 1; c <= end; c += descendants[c] + 1)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(buffer, digest[c]);
                hasher.Append(buffer);
            }

            digest[i] = hasher.GetCurrentHashAsUInt64();
        }

        return digest;
    }
}
