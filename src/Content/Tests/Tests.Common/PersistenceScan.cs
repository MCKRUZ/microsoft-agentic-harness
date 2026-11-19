using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Tests.Common;

/// <summary>
/// Reads the compiled production assemblies and reports, for every type, what it persists through and which
/// other production types it calls — the evidence a personal-data inventory guard needs to say "this type
/// stores data" without anyone having remembered to tell it (#899).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why compiled code, not source or a list of our own interfaces.</strong> A list of store interfaces
/// is exactly the hand-maintained list that let the conversation transcripts fall outside erasure: a store
/// nobody added to the list is invisible. This scan instead looks for calls into a fixed set of persistence
/// primitives — storage SDK assemblies, the file APIs that write, the distributed cache — which only grows
/// when a package is added, not whenever someone writes a new store.
/// </para>
/// <para>
/// <strong>Attribution.</strong> Lambdas, iterators and async methods compile into nested types, so every
/// call is credited to the outermost type that declared it. A store's helper class nested inside it is
/// credited to the store, which is the owner a reader would name.
/// </para>
/// <para>
/// <strong>What it cannot see.</strong> Data held only in memory (no primitive is called); persistence through
/// reflection or <c>dynamic</c>; native code reached through P/Invoke. Callers are found only through direct
/// calls, so a type that writes through an interface whose implementation is a channel is not attributed to
/// that channel. The guard built on this closes the first gap with a sibling rule; the others are stated in
/// its remarks rather than hidden.
/// </para>
/// </remarks>
public static class PersistenceScan
{
    /// <summary>What the scan learned about one production type.</summary>
    /// <param name="Assembly">The assembly that declares it.</param>
    /// <param name="Name">Its namespace-qualified name.</param>
    /// <param name="Primitives">The persistence primitives it calls or derives from, described for a reader.</param>
    /// <param name="Callees">Other production types it calls, by <see cref="KeyOf"/>.</param>
    /// <param name="Supertypes">Its base type and interfaces, by name (production and external).</param>
    /// <param name="Attributes">
    /// The attributes applied to it, by type name, each with its constructor arguments: strings as strings, enums
    /// as their underlying <see cref="int"/>.
    /// </param>
    /// <param name="IsInterface">Whether it is an interface, which declares members but holds nothing.</param>
    /// <param name="ExternalCalls">
    /// Every member of a non-production assembly it calls, as <c>Type::Member</c>, persistence or not; for guards that ask
    /// about one specific call, such as how SQLite connections are opened.
    /// </param>
    /// <param name="Declared">
    /// The type itself and every type nested in it, each with the fields that hold its state; for guards that ask what a
    /// type keeps in memory, which no call shows (#990).
    /// </param>
    public sealed record TypeFacts(
        string Assembly,
        string Name,
        bool IsInterface,
        IReadOnlySet<string> Primitives,
        IReadOnlySet<string> Callees,
        IReadOnlySet<string> Supertypes,
        IReadOnlyDictionary<string, IReadOnlyList<object?>> Attributes,
        IReadOnlySet<string> ExternalCalls,
        IReadOnlyList<DeclaredType> Declared);

    /// <summary>One type as compiled, nested or not, with the fields it declares.</summary>
    /// <param name="Name">
    /// Its namespace-qualified name, nested types joined with <c>+</c> (<c>Ns.Store+Entry</c>), the form a field signature
    /// names it by.
    /// </param>
    /// <param name="IsRecord">Whether it is a record, a value whose fields describe one thing rather than keep many.</param>
    /// <param name="IsCompilerGenerated">
    /// Whether the compiler emitted it (a lambda's closure, an iterator's or async method's state machine), whose fields
    /// live only as long as one call.
    /// </param>
    /// <param name="Fields">Its fields, constants left out.</param>
    public sealed record DeclaredType(string Name, bool IsRecord, bool IsCompilerGenerated, IReadOnlyList<FieldShape> Fields);

    /// <summary>One field, and the type it holds.</summary>
    /// <param name="Name">Its name; for an auto-property's backing field, the property's name.</param>
    /// <param name="IsBackingField">Whether the compiler emitted it for an auto-property.</param>
    /// <param name="Type">The type its signature names.</param>
    /// <param name="IsReadOnly">
    /// Whether only a constructor or initializer can set it: a <c>readonly</c> field, or a get-only or init-only
    /// auto-property's backing field.
    /// </param>
    public sealed record FieldShape(string Name, bool IsBackingField, TypeShape Type, bool IsReadOnly = false);

    /// <summary>A type as a signature names it, with its generic arguments.</summary>
    /// <param name="Assembly">The assembly that declares it; empty for a built-in type or a generic parameter.</param>
    /// <param name="Name">Its namespace-qualified name, nested types joined with <c>+</c>; generic parameters as <c>!</c>.</param>
    /// <param name="Arguments">Its generic arguments, or an array's element type.</param>
    /// <param name="IsArray">Whether it is an array, whose element is its one argument.</param>
    public sealed record TypeShape(string Assembly, string Name, IReadOnlyList<TypeShape> Arguments, bool IsArray)
    {
        /// <summary>This type and every type named inside it, depth first.</summary>
        public IEnumerable<TypeShape> SelfAndArguments() => Arguments.SelectMany(a => a.SelfAndArguments()).Prepend(this);
    }

    /// <summary>
    /// Storage SDK assemblies: any call into one of these, or deriving from one of its types, is persistence.
    /// Matched as the exact name or as a prefix followed by a dot, so <c>Microsoft.EntityFrameworkCore</c>
    /// also covers its relational and provider assemblies.
    /// </summary>
    private static readonly string[] StorageAssemblies =
    [
        "Npgsql",
        "Microsoft.Data.Sqlite",
        "Neo4j.Driver",
        "Azure.Search.Documents",
        "Azure.Storage.Blobs",
        "StackExchange.Redis",
        "Serilog.Sinks.File",
    ];

    /// <summary>
    /// The EF Core base type whose subclasses are a database. EF is matched by member below rather than by
    /// assembly, because most of its surface builds the model or wires DI and touches no data.
    /// </summary>
    private const string DbContextType = "Microsoft.EntityFrameworkCore.DbContext";

    /// <summary>
    /// Members that read or write stored data, by declaring type. A member name ending in <c>*</c> is a prefix;
    /// <c>*</c> alone is every member.
    /// </summary>
    private static readonly Dictionary<string, string[]> DataMembers = new(StringComparer.Ordinal)
    {
        [DbContextType] = ["SaveChanges*", "Add*", "Update*", "Remove*", "Attach*", "Find*"],
        ["Microsoft.EntityFrameworkCore.DbSet`1"] = ["*"],
        ["Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions"] = ["*"],
        ["Microsoft.EntityFrameworkCore.RelationalQueryableExtensions"] = ["FromSql*"],
        ["Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions"] = ["ExecuteSql*", "SqlQuery*"],
        // The response cache is written by the library, so the place that turns it on is the writer we can see.
        ["Microsoft.Extensions.AI.DistributedCachingChatClientBuilderExtensions"] = ["UseDistributedCache"],
        ["System.IO.File"] =
        [
            "WriteAll*", "AppendAll*", "AppendText", "Create*", "Open", "OpenWrite", "OpenHandle", "Copy", "Move",
            "Replace",
        ],
        ["System.IO.FileInfo"] = ["Create*", "AppendText", "Open", "OpenWrite", "CopyTo", "MoveTo", "Replace"],
        ["System.IO.FileStream"] = [".ctor"],
        ["System.IO.RandomAccess"] = ["Write*"],
        ["Microsoft.Extensions.Caching.Distributed.IDistributedCache"] = ["Set*"],
        ["Microsoft.Extensions.Caching.Distributed.DistributedCacheExtensions"] = ["Set*"],
        ["Microsoft.Extensions.Caching.Hybrid.HybridCache"] = ["GetOrCreate*", "Set*"],
    };

    /// <summary>ADO.NET's provider-neutral base types: any member is persistence.</summary>
    private const string AdoNetPrefix = "System.Data.Common.Db";

    private static readonly Dictionary<ushort, OperandType> Operands = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => unchecked((ushort)o.Value), o => o.OperandType);

    /// <summary>
    /// The compiled output of every production project under <paramref name="contentRoot"/>, built in the same
    /// configuration as the calling test. Throws when one is missing: a guard that silently scanned fewer
    /// assemblies than exist would pass for the wrong reason.
    /// </summary>
    public static IReadOnlyList<string> ProductionAssemblyPaths(string contentRoot)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var targetFramework = output.Name;
        var configuration = output.Parent!.Name;

        var paths = new List<string>();
        foreach (var project in Directory.EnumerateFiles(contentRoot, "*.csproj", SearchOption.AllDirectories))
        {
            if (SourceScan.IsExcluded(project, contentRoot))
                continue;

            var name = Path.GetFileNameWithoutExtension(project);
            var dll = Path.Combine(Path.GetDirectoryName(project)!, "bin", configuration, targetFramework, name + ".dll");
            if (!File.Exists(dll))
                throw new InvalidOperationException($"Production assembly not built: {dll}. Build the solution first.");

            paths.Add(dll);
        }

        return paths;
    }

    /// <summary>
    /// The key a type is listed under: its assembly and name. The assembly is part of it because two hosts
    /// each declare a top-level-statements <c>Program</c> in the global namespace.
    /// </summary>
    public static string KeyOf(string assembly, string name) => assembly + "/" + name;

    /// <summary>Scans each assembly and returns the facts for every outermost type, keyed by <see cref="KeyOf"/>.</summary>
    public static IReadOnlyDictionary<string, TypeFacts> Scan(IReadOnlyList<string> assemblyPaths)
    {
        ArgumentNullException.ThrowIfNull(assemblyPaths);

        var production = assemblyPaths.Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet(StringComparer.Ordinal);
        var builders = new Dictionary<string, Builder>(StringComparer.Ordinal);

        foreach (var path in assemblyPaths)
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var reader = pe.GetMetadataReader();
            var self = Path.GetFileNameWithoutExtension(path);

            foreach (var handle in reader.TypeDefinitions)
            {
                var definition = reader.GetTypeDefinition(handle);
                var owner = OutermostName(reader, handle);
                if (owner is "<Module>" or "" || owner.StartsWith("<", StringComparison.Ordinal))
                    continue; // Compiler-emitted module-level types: no declaring source type to credit.

                var key = KeyOf(self, owner);
                if (!builders.TryGetValue(key, out var builder))
                {
                    builder = new Builder(self, owner);
                    builders[key] = builder;
                }

                if (definition.GetDeclaringType().IsNil)
                    AddDeclaration(reader, self, definition, builder);

                builder.Declared.Add(Shape(reader, self, handle, definition));

                foreach (var methodHandle in definition.GetMethods())
                    AddCalls(pe, reader, self, production, methodHandle, builder);
            }
        }

        return builders.ToDictionary(
            b => b.Key,
            b => new TypeFacts(b.Value.Assembly, b.Value.Name, b.Value.IsInterface, b.Value.Primitives, b.Value.Callees, b.Value.Supertypes, b.Value.Attributes, b.Value.ExternalCalls, b.Value.Declared),
            StringComparer.Ordinal);
    }

    private static DeclaredType Shape(MetadataReader reader, string self, TypeDefinitionHandle handle, TypeDefinition definition)
    {
        var provider = new ShapeProvider(self);
        var fields = definition.GetFields()
            .Select(reader.GetFieldDefinition)
            .Where(f => (f.Attributes & FieldAttributes.Literal) == 0)
            .Select(f =>
            {
                var name = reader.GetString(f.Name);
                var backing = name.StartsWith('<') && name.EndsWith(">k__BackingField", StringComparison.Ordinal);
                return new FieldShape(
                    backing ? name[1..name.IndexOf('>')] : name,
                    backing,
                    f.DecodeSignature(provider, null),
                    (f.Attributes & FieldAttributes.InitOnly) != 0);
            })
            .ToList();

        // Every record, class or struct, is compiled with a PrintMembers method; nothing else declares one.
        var isRecord = definition.GetMethods().Any(m => reader.GetString(reader.GetMethodDefinition(m).Name) == "PrintMembers");
        var name = NestedName(reader, handle);
        return new DeclaredType(name, isRecord, reader.GetString(definition.Name).StartsWith('<'), fields);
    }

    /// <summary>A type definition's name with its declaring types, joined with <c>+</c> as a signature names it.</summary>
    private static string NestedName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        if (!definition.GetDeclaringType().IsNil)
            return NestedName(reader, definition.GetDeclaringType()) + "+" + reader.GetString(definition.Name);

        var ns = reader.GetString(definition.Namespace);
        var name = reader.GetString(definition.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static (string Assembly, string Name) NestedReferenceName(MetadataReader reader, TypeReferenceHandle handle, string self)
    {
        var reference = reader.GetTypeReference(handle);
        var scope = reference.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
        {
            var outer = NestedReferenceName(reader, (TypeReferenceHandle)scope, self);
            return (outer.Assembly, outer.Name + "+" + reader.GetString(reference.Name));
        }

        var assembly = scope.Kind == HandleKind.AssemblyReference
            ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : self;
        var ns = reader.GetString(reference.Namespace);
        var name = reader.GetString(reference.Name);
        return (assembly, string.IsNullOrEmpty(ns) ? name : ns + "." + name);
    }

    /// <summary>Decodes a field signature into the types it names, without loading the assemblies that define them.</summary>
    private sealed class ShapeProvider(string self) : ISignatureTypeProvider<TypeShape, object?>
    {
        private static TypeShape Leaf(string assembly, string name) => new(assembly, name, [], false);

        public TypeShape GetPrimitiveType(PrimitiveTypeCode typeCode) => Leaf("", "System." + typeCode);
        public TypeShape GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            Leaf(self, NestedName(reader, handle));
        public TypeShape GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var (assembly, name) = NestedReferenceName(reader, handle, self);
            return Leaf(assembly, name);
        }
        public TypeShape GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        public TypeShape GetSZArrayType(TypeShape elementType) => new("", "[]", [elementType], true);
        public TypeShape GetArrayType(TypeShape elementType, ArrayShape shape) => new("", "[]", [elementType], true);
        public TypeShape GetByReferenceType(TypeShape elementType) => elementType;
        public TypeShape GetPointerType(TypeShape elementType) => elementType;
        public TypeShape GetPinnedType(TypeShape elementType) => elementType;
        public TypeShape GetModifiedType(TypeShape modifier, TypeShape unmodifiedType, bool isRequired) => unmodifiedType;
        public TypeShape GetGenericInstantiation(TypeShape genericType, ImmutableArray<TypeShape> typeArguments) =>
            genericType with { Arguments = typeArguments };
        public TypeShape GetGenericTypeParameter(object? genericContext, int index) => Leaf("", "!" + index);
        public TypeShape GetGenericMethodParameter(object? genericContext, int index) => Leaf("", "!!" + index);
        public TypeShape GetFunctionPointerType(MethodSignature<TypeShape> signature) => Leaf("", "method*");
    }

    private sealed class Builder(string assembly, string name)
    {
        public string Assembly { get; } = assembly;
        public string Name { get; } = name;
        public bool IsInterface { get; set; }
        public HashSet<string> Primitives { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Callees { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Supertypes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<object?>> Attributes { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ExternalCalls { get; } = new(StringComparer.Ordinal);
        public List<DeclaredType> Declared { get; } = [];
    }

    private static void AddDeclaration(MetadataReader reader, string self, TypeDefinition definition, Builder builder)
    {
        builder.IsInterface = (definition.Attributes & System.Reflection.TypeAttributes.Interface) != 0;

        var supertypes = new List<EntityHandle>();
        if (!definition.BaseType.IsNil)
            supertypes.Add(definition.BaseType);
        supertypes.AddRange(definition.GetInterfaceImplementations().Select(i => reader.GetInterfaceImplementation(i).Interface));

        foreach (var supertype in supertypes)
        {
            if (TypeOf(reader, supertype, self) is not { } type)
                continue;

            builder.Supertypes.Add(type.Name);
            if (type.Name == DbContextType || IsStorageAssembly(type.Assembly))
                builder.Primitives.Add($"derives from {type.Name}");
        }

        foreach (var attributeHandle in definition.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(attributeHandle);
            var constructor = attribute.Constructor;
            var type = constructor.Kind switch
            {
                HandleKind.MethodDefinition => (self, OutermostName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType())),
                HandleKind.MemberReference => TypeOf(reader, reader.GetMemberReference((MemberReferenceHandle)constructor).Parent, self),
                _ => null,
            };

            if (type is { } t)
                builder.Attributes[t.Name] = DecodeArguments(attribute);
        }
    }

    private static IReadOnlyList<object?> DecodeArguments(CustomAttribute attribute)
    {
        try
        {
            return attribute.DecodeValue(EnumsAsInt32.Instance).FixedArguments.Select(a => a.Value).ToArray();
        }
        catch (Exception ex) when (ex is BadImageFormatException or NotSupportedException)
        {
            return []; // An argument shape this decoder does not model (a Type, an array): the name still counts.
        }
    }

    /// <summary>
    /// Decodes attribute arguments without loading the assemblies that define their types. Every enum is read as
    /// its underlying <see cref="int"/>, which holds for every enum this scan is asked to read.
    /// </summary>
    private sealed class EnumsAsInt32 : ICustomAttributeTypeProvider<string>
    {
        public static readonly EnumsAsInt32 Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => "enum";
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => "enum";
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type == "System.Type";
    }

    private static void AddCalls(
        PEReader pe, MetadataReader reader, string self, HashSet<string> production, MethodDefinitionHandle methodHandle, Builder builder)
    {
        var method = reader.GetMethodDefinition(methodHandle);
        if (method.RelativeVirtualAddress == 0)
            return;

        var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILReader();
        while (il.RemainingBytes > 0)
        {
            ushort code = il.ReadByte();
            if (code == 0xFE)
                code = (ushort)(0xFE00 | il.ReadByte());

            var operand = Operands[code];
            if (operand == OperandType.InlineMethod)
            {
                var token = MetadataTokens.EntityHandle(il.ReadInt32());
                if (Target(reader, token, self) is { } target)
                    Record(reader, token, target, production, builder);
                continue;
            }

            Skip(ref il, operand);
        }
    }

    private static void Record(
        MetadataReader reader, EntityHandle token, (string Assembly, string Type, string Member) target, HashSet<string> production, Builder builder)
    {
        if (production.Contains(target.Assembly))
        {
            var callee = KeyOf(target.Assembly, target.Type);
            if (callee != KeyOf(builder.Assembly, builder.Name))
                builder.Callees.Add(callee);
            return;
        }

        builder.ExternalCalls.Add($"{target.Type}::{target.Member}");
        if (IsPrimitive(reader, token, target))
            builder.Primitives.Add($"{target.Type}::{target.Member}");
    }

    private static bool IsPrimitive(MetadataReader reader, EntityHandle token, (string Assembly, string Type, string Member) target)
    {
        if (IsStorageAssembly(target.Assembly) || target.Type.StartsWith(AdoNetPrefix, StringComparison.Ordinal))
            return true;

        // A StreamWriter over a path writes a file; one over a stream writes wherever the stream goes, and the
        // stream's own construction is what this scan already catches.
        if (target is { Type: "System.IO.StreamWriter", Member: ".ctor" })
            return FirstParameterIsString(reader, token);

        return DataMembers.TryGetValue(target.Type, out var members)
            && members.Any(m => m.EndsWith('*')
                ? target.Member.StartsWith(m[..^1], StringComparison.Ordinal)
                : target.Member == m);
    }

    private static bool IsStorageAssembly(string assembly) =>
        StorageAssemblies.Any(s => assembly == s || assembly.StartsWith(s + ".", StringComparison.Ordinal));

    private static bool FirstParameterIsString(MetadataReader reader, EntityHandle token)
    {
        if (token.Kind != HandleKind.MemberReference)
            return false;

        var signature = reader.GetBlobReader(reader.GetMemberReference((MemberReferenceHandle)token).Signature);
        var header = signature.ReadSignatureHeader();
        if (header.IsGeneric)
            signature.ReadCompressedInteger();
        if (signature.ReadCompressedInteger() == 0)
            return false;

        signature.ReadSignatureTypeCode(); // return type: void for a constructor
        return signature.ReadSignatureTypeCode() == SignatureTypeCode.String;
    }

    private static (string Assembly, string Type, string Member)? Target(MetadataReader reader, EntityHandle handle, string self)
    {
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var definition = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return (self, OutermostName(reader, definition.GetDeclaringType()), reader.GetString(definition.Name));
            case HandleKind.MemberReference:
                var reference = reader.GetMemberReference((MemberReferenceHandle)handle);
                return TypeOf(reader, reference.Parent, self) is { } type
                    ? (type.Assembly, type.Name, reader.GetString(reference.Name))
                    : null;
            case HandleKind.MethodSpecification:
                return Target(reader, reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method, self);
            default:
                return null;
        }
    }

    private static (string Assembly, string Name)? TypeOf(MetadataReader reader, EntityHandle handle, string self)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                return (self, OutermostName(reader, (TypeDefinitionHandle)handle));
            case HandleKind.TypeReference:
                return ReferenceName(reader, (TypeReferenceHandle)handle, self);
            case HandleKind.TypeSpecification:
                var blob = reader.GetBlobReader(reader.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                if (blob.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                    return null; // An array, pointer or type parameter: nothing to attribute.
                blob.ReadByte();
                return TypeOf(reader, blob.ReadTypeHandle(), self);
            default:
                return null;
        }
    }

    private static (string Assembly, string Name) ReferenceName(MetadataReader reader, TypeReferenceHandle handle, string self)
    {
        var reference = reader.GetTypeReference(handle);
        var scope = reference.ResolutionScope;
        if (scope.Kind == HandleKind.TypeReference)
            return ReferenceName(reader, (TypeReferenceHandle)scope, self); // Nested: credit the outermost type.

        var assembly = scope.Kind == HandleKind.AssemblyReference
            ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : self;
        var ns = reader.GetString(reference.Namespace);
        var name = reader.GetString(reference.Name);
        return (assembly, string.IsNullOrEmpty(ns) ? name : ns + "." + name);
    }

    private static string OutermostName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        while (!definition.GetDeclaringType().IsNil)
            definition = reader.GetTypeDefinition(definition.GetDeclaringType());

        var ns = reader.GetString(definition.Namespace);
        var name = reader.GetString(definition.Name);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static void Skip(ref BlobReader il, OperandType operand)
    {
        var size = operand switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 * il.ReadInt32(),
            _ => 4,
        };

        for (var i = 0; i < size; i++)
            il.ReadByte();
    }
}
