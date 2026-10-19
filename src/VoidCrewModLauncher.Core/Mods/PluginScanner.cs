using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace VoidCrewModLauncher.Core.Mods;

/// <summary>A BepInEx plugin found in a DLL: its [BepInPlugin(GUID, Name, Version)].</summary>
public sealed record PluginInfo(string Guid, string Name, string Version, string FilePath);

/// <summary>
/// Reads the [BepInPlugin] attributes of DLLs without loading them (System.Reflection.Metadata), to learn which plugin
/// GUIDs a mod contains and what a DLL found on disk is.
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
                    if (!IsBepInPlugin(reader, attribute)) continue;
                    BlobReader blob = reader.GetBlobReader(attribute.Value);
                    if (blob.Length < 2 || blob.ReadUInt16() != 1) continue;
                    string? guid = blob.ReadSerializedString();
                    string? name = blob.ReadSerializedString();
                    string? version = blob.ReadSerializedString();
                    if (!string.IsNullOrEmpty(guid)) plugins.Add(new PluginInfo(guid, name ?? guid, version ?? "", dll));
                }
            }
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException or IOException)
        {
            // Native DLLs and other files that aren't .NET assemblies.
        }
        return plugins;
    }

    private static bool IsBepInPlugin(MetadataReader reader, CustomAttribute attribute)
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
            return reader.GetString(reference.Name) == "BepInPlugin" && reader.GetString(reference.Namespace) == "BepInEx";
        }
        if (type.Kind == HandleKind.TypeDefinition)
        {
            TypeDefinition definition = reader.GetTypeDefinition((TypeDefinitionHandle)type);
            return reader.GetString(definition.Name) == "BepInPlugin" && reader.GetString(definition.Namespace) == "BepInEx";
        }
        return false;
    }
}
