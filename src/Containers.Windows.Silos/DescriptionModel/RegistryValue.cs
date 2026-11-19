using Windows.Win32.System.Registry;

namespace Containers.Windows.Silos.DescriptionModel;

/// <summary>Raw native REG_* type and bytes; helper factories encode the schema's supported value types.</summary>
public sealed record RegistryValue
{
    public string Name { get; }
    public uint Type { get; }
    public byte[] Data { get; }

    /// <summary>False when the XML value element omitted every typed payload.</summary>
    public bool HasPayload { get; private init; } = true;

    /// <summary>Raw native REG_* type and bytes; helper factories encode the schema's supported value types.</summary>
    public RegistryValue(string name, uint type, byte[] data)
    {
        Name = name;
        Type = type;
        Data = data;
    }

    public static RegistryValue WithoutPayload(string name) => new RegistryValue(name, (uint)REG_VALUE_TYPE.REG_NONE, []) { HasPayload = false };
    public static RegistryValue String(string name, string value) => new RegistryValue(name, (uint)REG_VALUE_TYPE.REG_SZ, System.Text.Encoding.Unicode.GetBytes(value + '\0'));
    public static RegistryValue MultiString(string name, IEnumerable<string> values) => new RegistryValue(name, (uint)REG_VALUE_TYPE.REG_MULTI_SZ, System.Text.Encoding.Unicode.GetBytes(string.Join('\0', values) + "\0\0"));

    public static RegistryValue Dword(string name, uint value)
    {
        var data = new byte[sizeof(uint)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        return new RegistryValue(name, (uint)REG_VALUE_TYPE.REG_DWORD, data);
    }
}