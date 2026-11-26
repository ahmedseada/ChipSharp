using System.Reflection;
using ChipSharp.Abstractions;

namespace ChipSharp.Runtime.ArmV6M;

/// <summary>Reads the files a runtime ships inside its assembly: embedded resources named after the file.</summary>
public static class EmbeddedText
{
    /// <summary>The text of the resource <paramref name="fileName"/> in the assembly of <paramref name="owner"/>.</summary>
    public static string Read(Type owner, string fileName)
    {
        using var reader = new StreamReader(Open(owner, fileName));
        return reader.ReadToEnd();
    }

    /// <summary>The bytes of the resource <paramref name="fileName"/> in the assembly of <paramref name="owner"/>.</summary>
    public static byte[] ReadBytes(Type owner, string fileName)
    {
        using var stream = Open(owner, fileName);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    }

    /// <summary>True when the assembly of <paramref name="owner"/> has a resource named <paramref name="fileName"/>.</summary>
    public static bool Exists(Type owner, string fileName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        return owner.Assembly.GetManifestResourceInfo(fileName) is not null;
    }

    private static Stream Open(Type owner, string fileName)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        Assembly assembly = owner.Assembly;
        return assembly.GetManifestResourceStream(fileName)
            ?? throw new ChipSharpException($"The runtime file '{fileName}' is missing from {assembly.GetName().Name}.");
    }
}
