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
}
