using System.Buffers;
using System.Text;
using System.Xml;
using Windows.Win32.Data.Xml.XmlLite;
using Windows.Win32.Foundation;
using Windows.Win32.System.Registry;
using Containers.Windows.Silos.DescriptionModel;

namespace Containers.Windows.Silos;

/// <summary>Reads the Windows Server 2022 and 2025 Wc container-description XML grammar.</summary>
public static class SiloDescriptionXml
{
    /// <summary>Reads the first container description from a UTF-16 XML string.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="xml"/> is null.</exception>
    /// <exception cref="SiloException">A Wc schema field or the XML element depth is invalid.</exception>
    /// <exception cref="XmlException">The XML reader encountered malformed XML.</exception>
    public static SiloDescription Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);

        var terminator = xml.IndexOf('\0');
        var start = xml.Length != 0 && xml[0] == '\ufeff' ? 1 : 0;

        using var input = new StringReader(xml[start..(terminator < 0 ? xml.Length : terminator)]);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            ConformanceLevel = ConformanceLevel.Fragment,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        });

        return new Parser(reader).ReadDescription();
    }

    private static ReadOnlySpan<ushort> Zeroes => [0x0660, 0x06f0, 0x0966, 0x09e6, 0x0a66, 0x0ae6, 0x0b66, 0x0c66, 0x0ce6, 0x0d66, 0x0e50, 0x0ed0, 0x0f20, 0x1040, 0x17e0, 0x1810, 0xff10];

    internal static uint IntegerDigit(char c)
    {
        if (c <= 0x7f)
            return HexDigit(c);

        foreach (var zero in Zeroes)
        {
            var offset = unchecked((uint)(c - zero));
            if (offset < 10)
                return offset;
        }

        return uint.MaxValue;
    }

    private static uint HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => (uint)(c - '0'),
        >= 'a' and <= 'f' => (uint)(c - 'a' + 10),
        >= 'A' and <= 'F' => (uint)(c - 'A' + 10),
        _ => uint.MaxValue,
    };

    private sealed class Parser(XmlReader reader)
    {
        private const int MaximumElementDepth = 256;
        private string? firstError;

        internal SiloDescription ReadDescription()
        {
            while (Read() && reader.NodeType is XmlNodeType.XmlDeclaration or XmlNodeType.Comment or XmlNodeType.Whitespace)
            {
                // permit these nodes before the root.
            }

            var description = new SiloDescription();
            if (reader.NodeType == XmlNodeType.Element && FoldAscii(reader.LocalName) == "container")
            {
                ReadAttributes();
                ReadChildren(new Child("namespace", () => description = ReadNamespace()));
            }
            else
            {
                Report("expected a container element");
            }

            if (firstError is not null)
                SiloException.CheckWin32((int)WIN32_ERROR.ERROR_XML_PARSE_ERROR, $"Parse silo description XML ({firstError})");

            return description;
        }

        private SiloDescription ReadNamespace()
        {
            ReadAttributes();

            var description = new SiloDescription();
            ReadChildren(
                new Child("job", () => description = description with { Job = ReadJob() }),
                new Child("filesystem", () => description = description with { FileSystem = ReadFileSystem() }),
                new Child("mountmgr", () => description = description with { Mounts = ReadList("mount_point", ReadMount) }),
                new Child("namedpipe", () => description = description with { NamedPipes = ReadList("symlink", ReadPipe) }),
                new Child("ob", () => description = description with { ObjectNamespace = ReadObjects() }),
                new Child("registry", () => description = description with { Registry = ReadRegistry() }),
                new Child("network", () => description = description with { NetworkCompartment = ReadNetwork() }),
                new Child("device", () => description = description with { Device = ReadDevice() }),
                new Child("hostfiles", () => description = description with { Downlevel = ReadHostFiles() }));

            return description;
        }

        private JobConfiguration ReadJob()
        {
            var attributes = ReadAttributes("threadimpersonation");
            var job = new JobConfiguration { EnableThreadImpersonation = ReadBoolean(attributes.GetValueOrDefault("threadimpersonation")) };
            ReadChildren(
                new Child("cpu", () =>
                {
                    var cpu = ReadAttributes("rate", "weight");
                    job = job with { Cpu = new CpuConfiguration(ReadUInt32(cpu.GetValueOrDefault("weight")), ReadUInt32(cpu.GetValueOrDefault("rate"))) };
                    ReadChildren();
                }),
                new Child("memory", () =>
                {
                    var memory = ReadAttributes("limit");
                    job = job with { MemoryLimit = ReadUInt64(memory.GetValueOrDefault("limit")) };
                    ReadChildren();
                }),
                new Child("systemroot", () =>
                {
                    var systemRoot = ReadAttributes("path");
                    job = job with { SystemRoot = systemRoot.GetValueOrDefault("path", "") };
                    ReadChildren();
                }),
                new Child("terminationpolicy", () => job = job with
                {
                    KillOnLastHandleClose = ReadChoice(ReadText(), "permanentquiescent", "killonhandleclose") == 1,
                }));

            return job;
        }

        private FileSystemConfiguration ReadFileSystem()
        {
            var attributes = ReadAttributes("path", "isolation", "nesting");
            var layers = new List<WciLayer>();
            var bindings = new List<BindBatch>();

            var wci = new WciConfiguration(attributes.GetValueOrDefault("path", ""))
            {
                IsolationMode = ReadChoice(attributes.GetValueOrDefault("isolation"), "hard", "soft"),
                NestingMode = ReadChoice(attributes.GetValueOrDefault("nesting"), "inner", "outer"),
            };

            ReadChildren(
                new Child("layer", () => layers.Add(ReadFileSystemLayer()), isRepeated: true),
                new Child("bindings", () => bindings.Add(ReadBindings()), isRepeated: true));

            return new FileSystemConfiguration { Wci = wci with { Layers = layers.ToArray() }, Batches = bindings.ToArray() };
        }

        private WciLayer ReadFileSystemLayer()
        {
            var attributes = ReadAttributes("identifier", "path");
            var layer = new WciLayer(ReadGuid(attributes.GetValueOrDefault("identifier")), attributes.GetValueOrDefault("path", ""));
            ReadChildren();
            return layer;
        }

        private BindBatch ReadBindings()
        {
            ReadAttributes();

            var path = "";
            var roots = new List<string>();
            ReadChildren(
                new Child("filepath", () => path = ReadText()),
                new Child("bindingroots", () => roots.Add(ReadText()), isRepeated: true));

            return new BindBatch(path) { Exceptions = roots.ToArray() };
        }

        private DriveMapping ReadMount()
        {
            var attributes = ReadAttributes("name", "path");
            var name = attributes.GetValueOrDefault("name", "");
            var mount = new DriveMapping(name.Length == 0 ? '\0' : name[0], attributes.GetValueOrDefault("path", ""));
            ReadChildren();
            return mount;
        }

        private PipeMapping ReadPipe()
        {
            var attributes = ReadAttributes("name", "path");
            var pipe = new PipeMapping(attributes.GetValueOrDefault("name", ""), attributes.GetValueOrDefault("path", ""));
            ReadChildren();
            return pipe;
        }

        private ObjectNamespaceConfiguration ReadObjects()
        {
            var attributes = ReadAttributes("shadow");
            var directories = new List<ObjectDirectory>();
            var links = new List<ObjectSymlink>();
            var shadow = ReadChoice(attributes.GetValueOrDefault("shadow"), "false", "true", "ifexists") != 0;
            ReadChildren(
                new Child("objdir", () => directories.Add(ReadDirectory()), isRepeated: true),
                new Child("symlink", () => links.Add(ReadObjectLink()), isRepeated: true));

            return new ObjectNamespaceConfiguration { ShadowRoot = shadow, Directories = directories.ToArray(), Symlinks = links.ToArray() };
        }

        private ObjectDirectory ReadDirectory()
        {
            var attributes = ReadAttributes("name", "clonesd", "shadow");
            var directories = new List<ObjectDirectory>();
            var links = new List<ObjectSymlink>();
            var directory = new ObjectDirectory(attributes.GetValueOrDefault("name", ""))
            {
                SecurityDescriptorSource = attributes.GetValueOrDefault("clonesd"),
                Shadow = (DirectoryShadow)ReadChoice(attributes.GetValueOrDefault("shadow"), "false", "true", "ifexists"),
            };

            ReadChildren(
                new Child("objdir", () => directories.Add(ReadDirectory()), isRepeated: true),
                new Child("symlink", () => links.Add(ReadObjectLink()), isRepeated: true));

            return directory with { Directories = directories.ToArray(), Symlinks = links.ToArray() };
        }

        private ObjectSymlink ReadObjectLink()
        {
            var attributes = ReadAttributes("name", "path", "scope", "pathtoclone", "access_mask");
            var link = new ObjectSymlink(attributes.GetValueOrDefault("name", ""))
            {
                Target = attributes.GetValueOrDefault("path"),
                TargetSource = attributes.GetValueOrDefault("pathtoclone"),
                Scope = (SymlinkScope)ReadChoice(attributes.GetValueOrDefault("scope"), "local", "global", "globaldropsilo"),
                AccessMask = ReadUInt32(attributes.GetValueOrDefault("access_mask")),
            };
            ReadChildren();
            return link;
        }

        private RegistryConfiguration ReadRegistry()
        {
            ReadAttributes();
            var symlinks = new List<RegistrySymlink>();
            var redirections = new List<RegistryRedirection>();
            var stacks = new List<RegistryHiveStack>();
            ReadChildren(
                new Child("symlink", () => symlinks.Add(ReadRegistryLink()), isRepeated: true),
                new Child("redirectionnode", () => redirections.Add(ReadRedirection()), isRepeated: true),
                new Child("hivestack", () => stacks.Add(ReadHiveStack()), isRepeated: true));

            return new RegistryConfiguration { Symlinks = symlinks.ToArray(), Redirections = redirections.ToArray(), HiveStacks = stacks.ToArray() };
        }

        private RegistrySymlink ReadRegistryLink()
        {
            var attributes = ReadAttributes("key", "target");
            var link = new RegistrySymlink(attributes.GetValueOrDefault("key", ""), attributes.GetValueOrDefault("target", ""));
            ReadChildren();
            return link;
        }

        private RegistryRedirection ReadRedirection()
        {
            var attributes = ReadAttributes("containerpath", "hostpath", "hivestack", "access_mask", "trustedhive", "exitnode");
            var redirection = new RegistryRedirection(attributes.GetValueOrDefault("containerpath", ""))
            {
                Target = attributes.GetValueOrDefault("hostpath"),
                HiveStack = attributes.GetValueOrDefault("hivestack"),
                AccessMask = ReadUInt32(attributes.GetValueOrDefault("access_mask")),
                Trusted = ReadBoolean(attributes.GetValueOrDefault("trustedhive")),
                ExitNode = ReadBoolean(attributes.GetValueOrDefault("exitnode")),
            };
            ReadChildren();
            return redirection;
        }

        private RegistryHiveStack ReadHiveStack()
        {
            var attributes = ReadAttributes("hive");
            var hives = new List<RegistryHive>();
            var keys = new List<RegistryKeyConfiguration>();
            var deleteKeys = new List<string>();
            ReadChildren(
                new Child("layer", () => hives.Add(ReadRegistryHive()), isRepeated: true),
                new Child("mkkey", () => keys.Add(ReadRegistryKey()), isRepeated: true),
                new Child("delkey", () => deleteKeys.Add(ReadDeleteKey()), isRepeated: true));

            return new RegistryHiveStack(attributes.GetValueOrDefault("hive", ""))
            {
                Hives = hives.ToArray(),
                Keys = keys.ToArray(),
                DeleteKeys = deleteKeys.ToArray(),
            };
        }

        private RegistryHive ReadRegistryHive()
        {
            var attributes = ReadAttributes("hosthive", "filepath", "identifier", "readonly", "immutable", "inherittrustclass", "trustedhive", "writethrough");
            ulong fileToken = 0;
            var hive = new RegistryHive
            {
                ExistingPath = attributes.GetValueOrDefault("hosthive"),
                FilePath = attributes.GetValueOrDefault("filepath", ""),
                MountId = ReadGuid(attributes.GetValueOrDefault("identifier")),
                ReadOnly = ReadBoolean(attributes.GetValueOrDefault("readonly")),
                Immutable = ReadBoolean(attributes.GetValueOrDefault("immutable")),
                InheritTrust = ReadBoolean(attributes.GetValueOrDefault("inherittrustclass")),
                TrustedHive = ReadBoolean(attributes.GetValueOrDefault("trustedhive")),
                WriteThrough = ReadBoolean(attributes.GetValueOrDefault("writethrough")),
            };
            ReadChildren(new Child("fileaccesstoken", () => fileToken = ReadUInt64(ReadText())));
            return hive with { FileAccessToken = fileToken };
        }

        private RegistryKeyConfiguration ReadRegistryKey()
        {
            var attributes = ReadAttributes("name", "clonesd", "volatile");
            var keys = new List<RegistryKeyConfiguration>();
            var values = new List<RegistryValue>();
            var key = new RegistryKeyConfiguration(attributes.GetValueOrDefault("name", ""))
            {
                SecurityDescriptorSource = attributes.GetValueOrDefault("clonesd"),
                Volatile = ReadBoolean(attributes.GetValueOrDefault("volatile")),
            };
            ReadChildren(
                new Child("mkkey", () => keys.Add(ReadRegistryKey()), isRepeated: true),
                new Child("mkvalue", () => values.Add(ReadRegistryValue()), isRepeated: true));

            return key with { Keys = keys.ToArray(), Values = values.ToArray() };
        }

        private string ReadDeleteKey()
        {
            var attributes = ReadAttributes("name");
            ReadChildren();
            return attributes.GetValueOrDefault("name", "");
        }

        private RegistryValue ReadRegistryValue()
        {
            var attributes = ReadAttributes("name", "data_multistring", "data_dword", "data_string", "data_binary");
            var name = attributes.GetValueOrDefault("name", "");
            var multiString = attributes.GetValueOrDefault("data_multistring");
            uint? dword = attributes.TryGetValue("data_dword", out var number) ? ReadUInt32(number) : null;
            var text = attributes.GetValueOrDefault("data_string");
            var binary = attributes.TryGetValue("data_binary", out var hex) ? ReadHex(hex) : null;
            ReadChildren();

            if (binary is not null)
                return new RegistryValue(name, (uint)REG_VALUE_TYPE.REG_BINARY, binary);
            if (dword is { } value)
                return RegistryValue.Dword(name, value);
            if (multiString is not null)
                return new RegistryValue(name, (uint)REG_VALUE_TYPE.REG_MULTI_SZ, Encoding.Unicode.GetBytes(multiString + '\0'));
            if (text is not null)
                return RegistryValue.String(name, text);

            return RegistryValue.WithoutPayload(name);
        }

        private string ReadNetwork()
        {
            var attributes = ReadAttributes("compartment");
            ReadChildren();
            return attributes.GetValueOrDefault("compartment", "");
        }

        private DeviceConfiguration ReadDevice()
        {
            var attributes = ReadAttributes("requires_driverstore");
            var categories = new List<DeviceCategory>();
            var instances = new List<DeviceInstance>();
            var requiresDriverStore = ReadBoolean(attributes.GetValueOrDefault("requires_driverstore"));
            ReadChildren(
                new Child("device_category", () =>
                {
                    var category = ReadAttributes("name");
                    categories.Add(new DeviceCategory(category.GetValueOrDefault("name", ""), ReadList("interface_class", ReadInterfaceClass)));
                }, isRepeated: true),
                new Child("device_instance", () =>
                {
                    var instance = ReadAttributes("id", "location_path", "port_name");
                    instances.Add(new DeviceInstance(instance.GetValueOrDefault("id", ""), instance.GetValueOrDefault("location_path", ""), instance.GetValueOrDefault("port_name", ""), ReadList("interface_class", ReadInterfaceClass)));
                }, isRepeated: true));

            return new DeviceConfiguration
            {
                RequiresDriverStore = requiresDriverStore,
                Categories = categories.ToArray(),
                Instances = instances.ToArray(),
            };
        }

        private DeviceInterfaceClass ReadInterfaceClass()
        {
            var attributes = ReadAttributes("type", "identifier", "recurse");
            var type = (DeviceInterfaceClassType)ReadChoice(attributes.GetValueOrDefault("type"), "inherit", "deviceinstance", "classguid");
            var id = ReadGuid(attributes.GetValueOrDefault("identifier"));
            var recurse = ReadBoolean(attributes.GetValueOrDefault("recurse"), defaultValue: true);
            ReadChildren();
            return new DeviceInterfaceClass(type, id, recurse);
        }

        private DownlevelConfiguration ReadHostFiles()
        {
            var attributes = ReadAttributes("base_image_path", "sandbox_path");
            return new DownlevelConfiguration(attributes.GetValueOrDefault("sandbox_path", ""), ReadList("file", ReadText))
            {
                BaseImagePath = attributes.GetValueOrDefault("base_image_path"),
            };
        }

        private IReadOnlyList<T> ReadList<T>(string name, Func<T> readItem)
        {
            var items = new List<T>();
            ReadChildren(new Child(name, () => items.Add(readItem()), isRepeated: true));
            return items.ToArray();
        }

        private Dictionary<string, string> ReadAttributes(params string[] allowed)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if (reader.MoveToFirstAttribute())
            {
                do
                {
                    var name = FoldAscii(reader.LocalName);
                    if (!allowed.Contains(name))
                        Report($"unknown attribute '{reader.Name}'");
                    else if (!values.TryAdd(name, reader.Value))
                        Report($"duplicate attribute '{reader.Name}'");
                } while (reader.MoveToNextAttribute());

                reader.MoveToElement();
            }

            return values;
        }

        // A field reader leaves the cursor on its end tag, or on its start tag when self-closing.
        private void ReadChildren(params Child[] children)
        {
            if (reader.IsEmptyElement)
                return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (Read() && reader.NodeType != XmlNodeType.EndElement)
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var name = FoldAscii(reader.LocalName);
                        var field = Array.Find(children, item => item.Name == name);
                        if (field.Read is null)
                        {
                            Report($"unknown element '{reader.Name}'");
                            SkipElement();
                        }
                        else if (field.IsRepeated || seen.Add(name))
                        {
                            field.Read();
                        }
                        else
                        {
                            Report($"duplicate element '{reader.Name}'");
                        }

                        break;
                    case XmlNodeType.Comment:
                    case XmlNodeType.Whitespace:
                    case XmlNodeType.SignificantWhitespace:
                    case XmlNodeType.XmlDeclaration:
                        break;
                    default:
                        Report($"unexpected {reader.NodeType} in an object element");
                        return;
                }
            }
        }

        private string ReadText()
        {
            if (reader.IsEmptyElement)
                return "";

            var text = new StringBuilder();
            while (Read() && reader.NodeType != XmlNodeType.EndElement)
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Text:
                    case XmlNodeType.CDATA:
                        text.Append(reader.Value);
                        break;
                    case XmlNodeType.Comment:
                        break;
                    default:
                        Report($"unexpected {reader.NodeType} in a scalar element");
                        // The field handler returns at the offending node. Its
                        // enclosing object reader performs the next Read.
                        return "";
                }
            }

            return text.ToString();
        }

        private void SkipElement()
        {
            if (reader.IsEmptyElement)
                return;

            var depth = reader.Depth;
            while (Read() && !(reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth))
            {
                // Skip
            }
        }

        private bool Read()
        {
            var read = reader.Read();
            if (read && reader.NodeType == XmlNodeType.Element && reader.Depth >= MaximumElementDepth)
                SiloException.CheckHResult((int)XmlError.SC_E_MAXELEMENTDEPTH, "Read silo description XML");
            return read;
        }

        private bool ReadBoolean(string? value, bool defaultValue = false) =>
            value is null ? defaultValue : ReadChoice(value, "false", "true") == 1;

        private uint ReadChoice(string? value, params string[] choices)
        {
            if (value is null)
                return 0;

            var index = Array.IndexOf(choices, FoldAscii(value));
            if (index >= 0)
                return (uint)index;

            Report($"invalid value '{value}'");
            return 0;
        }

        private uint ReadUInt32(string? value)
        {
            var number = ReadUInt64(value);
            if (number <= uint.MaxValue)
                return (uint)number;

            Report("unsigned 32-bit value is out of range");
            return 0;
        }

        private ulong ReadUInt64(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return 0;

            var digits = value.AsSpan().TrimStart();
            var negative = digits.StartsWith("-");
            if (!digits.IsEmpty && (digits[0] is '+' or '-'))
                digits = digits[1..];

            uint radix = 10;
            if (!digits.IsEmpty && IntegerDigit(digits[0]) == 0)
            {
                radix = 8;
                if (digits.Length >= 3 && digits[1] is 'x' or 'X' && IntegerDigit(digits[2]) < 16)
                {
                    radix = 16;
                    digits = digits[2..];
                }
            }

            if (digits.IsEmpty)
            {
                Report($"invalid unsigned integer '{value}'");
                return 0;
            }

            ulong number = 0;
            var overflow = false;
            foreach (var c in digits)
            {
                var digit = IntegerDigit(c);
                if (digit >= radix)
                {
                    Report($"invalid unsigned integer '{value}'");
                    return 0;
                }

                if (number > (ulong.MaxValue - digit) / radix)
                    overflow = true;
                else if (!overflow)
                    number = number * radix + digit;
            }

            return overflow ? ulong.MaxValue : negative ? unchecked(0ul - number) : number;
        }

        private Guid ReadGuid(string? value)
        {
            if (value is null)
                return Guid.Empty;

            if (value.Length == 36 && value.Count(c => c == '-') == 4 && value.All(c => c == '-' || HexDigit(c) < 16) && Guid.TryParseExact(value, "D", out var identifier))
                return identifier;

            Report($"invalid GUID '{value}'");
            return Guid.Empty;
        }

        private byte[] ReadHex(string value)
        {
            var bytes = new byte[value.Length / 2];
            if (value.Length % 2 == 0 &&
                Convert.FromHexString(value, bytes, out _, out _) == OperationStatus.Done)
                return bytes;

            Report("invalid hexadecimal binary value");
            return [];
        }

        private static string FoldAscii(string value) => value.Any(c => c is >= 'A' and <= 'Z')
            ? string.Create(value.Length, value, static (span, source) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + ('a' - 'A')) : source[i];
            })
            : value;

        private void Report(string error) => firstError ??= $"{error} at line {((IXmlLineInfo)reader).LineNumber}";

        private readonly record struct Child
        {
            public Child(string name, Action read, bool isRepeated = false)
            {
                Name = name;
                Read = read;
                IsRepeated = isRepeated;
            }

            public string Name { get; init; }
            public Action Read { get; init; }
            public bool IsRepeated { get; init; }
        }
    }
}