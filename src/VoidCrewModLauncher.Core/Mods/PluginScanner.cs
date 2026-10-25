using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using VoidCrewModLauncher.Protocol;

namespace VoidCrewModLauncher.Core.Mods;

/// <summary>
/// A BepInEx plugin found in a DLL: its [BepInPlugin(GUID, Name, Version)], and the VoidManager multiplayer type its DLL
/// declares (<see cref="Protocol.VoidManagerType"/> as a number; null when the DLL decides it while the game runs).
/// </summary>
public sealed record PluginInfo(string Guid, string Name, string Version, string FilePath, int? VoidManagerType = (int)Protocol.VoidManagerType.Unmanaged);

/// <summary>
/// Reads the [BepInPlugin] attributes of DLLs without loading them (System.Reflection.Metadata), to learn which plugin
/// GUIDs a mod contains and what a DLL found on disk is; and what VoidManager will make of the plugin, so the launcher
/// knows which mods VoidManager checks on joining players before the game runs.
/// </summary>
public static class PluginScanner
{
    public static List<PluginInfo> ScanDirectory(string directory)
    {
        var plugins = new List<PluginInfo>();
        if (!Directory.Exists(directory)) return plugins;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (string dll in Directory.EnumerateFiles(directory, "*.dll", options))
        {
            plugins.AddRange(ScanFile(dll));
        }
        return plugins;
    }

    public static List<PluginInfo> ScanFile(string dll)
    {
        var plugins = new List<PluginInfo>();
        try
        {
            using FileStream stream = File.OpenRead(dll);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return plugins;
            MetadataReader reader = pe.GetMetadataReader();
            foreach (TypeDefinitionHandle typeHandle in reader.TypeDefinitions)
            {
                TypeDefinition type = reader.GetTypeDefinition(typeHandle);
                foreach (CustomAttributeHandle attributeHandle in type.GetCustomAttributes())
                {
                    CustomAttribute attribute = reader.GetCustomAttribute(attributeHandle);
                    if (!IsAttribute(reader, attribute, "BepInEx", "BepInPlugin")) continue;
                    BlobReader blob = reader.GetBlobReader(attribute.Value);
                    if (blob.Length < 2 || blob.ReadUInt16() != 1) continue;
                    string? guid = blob.ReadSerializedString();
                    string? name = blob.ReadSerializedString();
                    string? version = blob.ReadSerializedString();
                    if (!string.IsNullOrEmpty(guid)) plugins.Add(new PluginInfo(guid, name ?? guid, version ?? "", dll));
                }
            }
            if (plugins.Count > 0)
            {
                // VoidManager leaves itself out of its checks (its DLL holds VoidPlugin's own classes).
                int? type = plugins.Any(p => p.Guid == ModPolicy.VoidManagerGuid) ? (int)VoidManagerType.Unmanaged : ReadVoidManagerType(pe, reader);
                plugins = plugins.Select(p => p with { VoidManagerType = type }).ToList();
            }
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Native DLLs and other files that aren't .NET assemblies, and files Windows won't let the launcher read (it
            // reads the store's DLLs at start-up: one of those mustn't stop it starting).
        }
        return plugins;
    }

    /// <summary>
    /// The strictest VoidManager type of a mod's plugins: what VoidManager checks the mod for. Unmanaged when no plugin
    /// registers with VoidManager; null when one of them decides while the game runs (and nothing stricter is known).
    /// </summary>
    public static int? StrictestType(IEnumerable<PluginInfo> plugins)
    {
        int strictest = (int)VoidManagerType.Unmanaged;
        bool unknown = false;
        foreach (PluginInfo plugin in plugins)
        {
            if (plugin.VoidManagerType is int type) strictest = Math.Max(strictest, type);
            else unknown = true;
        }
        return unknown && strictest < (int)VoidManagerType.All ? null : strictest;
    }

    /// <summary>
    /// What VoidManager makes of the plugins in a DLL: VoidManager gives each plugin the multiplayer type of the first
    /// class in its DLL that derives from VoidManager.VoidPlugin, and Unmanaged without one. The type is read from the
    /// class's MPType getter when that returns a constant (as it nearly always does), else from the DLL's own
    /// [AssemblyMetadata("MPType", ...)] when it has one; VoidPlugin's default is Session.
    /// </summary>
    private static int? ReadVoidManagerType(PEReader pe, MetadataReader reader)
    {
        try
        {
            return VoidPluginType(pe, reader);
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or ArgumentException)
        {
            // A DLL whose method bodies can't be read: the plugin is still listed, its type unknown.
            return null;
        }
    }

    private static int? VoidPluginType(PEReader pe, MetadataReader reader)
    {
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            if ((type.Attributes & (TypeAttributes.Abstract | TypeAttributes.Interface)) != 0 || !IsVoidPlugin(reader, type.BaseType, 0)) continue;
            // The getter of the class or of a class between it and VoidPlugin.
            TypeDefinition? current = type;
            for (int depth = 0; current is { } level && depth < 8; depth++)
            {
                foreach (MethodDefinitionHandle methodHandle in level.GetMethods())
                {
                    MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                    if (reader.GetString(method.Name) != "get_MPType") continue;
                    return ConstantResult(pe, method) ?? DeclaredType(reader);
                }
                current = level.BaseType.Kind == HandleKind.TypeDefinition ? reader.GetTypeDefinition((TypeDefinitionHandle)level.BaseType) : null;
            }
            return (int)VoidManagerType.Session;
        }
        return (int)VoidManagerType.Unmanaged;
    }

    private static bool IsVoidPlugin(MetadataReader reader, EntityHandle baseType, int depth)
    {
        if (baseType.IsNil || depth > 8) return false;
        if (baseType.Kind == HandleKind.TypeReference)
        {
            TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)baseType);
            return reader.GetString(reference.Name) == "VoidPlugin" && reader.GetString(reference.Namespace) == "VoidManager";
        }
        if (baseType.Kind != HandleKind.TypeDefinition) return false;
        TypeDefinition definition = reader.GetTypeDefinition((TypeDefinitionHandle)baseType);
        if (reader.GetString(definition.Name) == "VoidPlugin" && reader.GetString(definition.Namespace) == "VoidManager") return true;
        return IsVoidPlugin(reader, definition.BaseType, depth + 1);
    }

    /// <summary>
    /// The value a method returns when its body only loads a constant: "ldc.i4 N; ret" (release builds), or with the
    /// debug build's "stloc.0; br.s; ldloc.0" in between. Null for anything else.
    /// </summary>
    private static int? ConstantResult(PEReader pe, MethodDefinition method)
    {
        if (method.RelativeVirtualAddress == 0) return null;
        byte[]? il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
        if (il == null) return null;
        int i = 0;
        void SkipNops()
        {
            while (i < il.Length && il[i] == 0x00) i++;
        }

        SkipNops();
        if (i >= il.Length) return null;
        int value;
        byte op = il[i++];
        if (op is >= 0x15 and <= 0x1E) value = op - 0x16; // ldc.i4.m1 .. ldc.i4.8
        else if (op == 0x1F && i < il.Length) value = (sbyte)il[i++]; // ldc.i4.s
        else if (op == 0x20 && i + 4 <= il.Length)
        {
            value = BitConverter.ToInt32(il, i); // ldc.i4
            i += 4;
        }
        else return null;
        SkipNops();
        if (i < il.Length && il[i] == 0x0A) // stloc.0
        {
            i++;
            SkipNops();
            if (i + 1 < il.Length && il[i] == 0x2B) i += 2; // br.s
            SkipNops();
            if (i >= il.Length || il[i] != 0x06) return null; // ldloc.0
            i++;
            SkipNops();
        }
        return i < il.Length && il[i] == 0x2A ? value : null; // ret
    }

    /// <summary>The DLL's [assembly: AssemblyMetadata("MPType", "All")] (a name or number), when it says.</summary>
    private static int? DeclaredType(MetadataReader reader)
    {
        if (!reader.IsAssembly) return null;
        foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (!IsAttribute(reader, attribute, "System.Reflection", "AssemblyMetadataAttribute")) continue;
            BlobReader blob = reader.GetBlobReader(attribute.Value);
            if (blob.Length < 2 || blob.ReadUInt16() != 1) continue;
            string? key = blob.ReadSerializedString();
            string? value = blob.ReadSerializedString();
            if (!string.Equals(key, "MPType", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(value)) continue;
            if (Enum.TryParse(value.Trim(), true, out VoidManagerType parsed) && Enum.IsDefined(parsed)) return (int)parsed;
        }
        return null;
    }

    private static bool IsAttribute(MetadataReader reader, CustomAttribute attribute, string ns, string name)
    {
        EntityHandle type;
        if (attribute.Constructor.Kind == HandleKind.MemberReference)
        {
            type = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
        }
        else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
        {
            type = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
        }
        else
        {
            return false;
        }

        if (type.Kind == HandleKind.TypeReference)
        {
            TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)type);
            return reader.GetString(reference.Name) == name && reader.GetString(reference.Namespace) == ns;
        }
        if (type.Kind == HandleKind.TypeDefinition)
        {
            TypeDefinition definition = reader.GetTypeDefinition((TypeDefinitionHandle)type);
            return reader.GetString(definition.Name) == name && reader.GetString(definition.Namespace) == ns;
        }
        return false;
    }
}
