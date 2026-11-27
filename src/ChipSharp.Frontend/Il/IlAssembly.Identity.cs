using System.Globalization;
using System.Security.Cryptography;

namespace ChipSharp.Frontend.Il;

public sealed partial class IlAssembly
{
    /// <summary>
    /// The assembly's display name as .NET writes it in a type's assembly-qualified name, e.g. "Program, Version=1.0.0.0,
    /// Culture=neutral, PublicKeyToken=null": what <c>typeof(Box&lt;Node&gt;).FullName</c> names Node's assembly with.
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (!_reader.IsAssembly)
            {
                return $"{Name}, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";
            }

            var definition = _reader.GetAssemblyDefinition();
            var culture = _reader.GetString(definition.Culture);
            var key = _reader.GetBlobBytes(definition.PublicKey);

            // a public key's token: the last 8 bytes of its SHA-1 hash, the other way round
            var token = key.Length == 0 ? "null"
                : string.Concat(SHA1.HashData(key).TakeLast(8).Reverse().Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
            return string.Create(CultureInfo.InvariantCulture,
                $"{Name}, Version={definition.Version}, Culture={(culture.Length == 0 ? "neutral" : culture)}, PublicKeyToken={token}");
        }
    }
}
