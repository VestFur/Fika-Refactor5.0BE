using System;
using System.Linq;
using System.Reflection;
using EFT;
using Fika.Core.Networking.Serialization;
using Il2CppInterop.Runtime.InteropTypes;

namespace Fika.Core.Main.Utils;

/// <summary>
/// Serializes the raid-end profile descriptor for the server.
/// <para>
/// The original call - <c>descriptor.ToUnparsedData(new Il2CppReferenceArray&lt;JsonConverter&gt;(0)).JToken.ToString()</c> -
/// requires the IL2CPP interop proxy of Newtonsoft.Json. When a managed
/// Newtonsoft.Json.dll in BepInEx/plugins shadows that proxy, the converter
/// array cannot be constructed and the JToken member stops resolving, which
/// aborted the whole patch and lost the scav save.
/// </para>
/// <para>
/// Both forms produce the descriptor serialized with the game's own converters,
/// so the shadowed install falls back to serializing the descriptor directly
/// with the game's JsonConvert through IL2CPP - the same call the JSON bridge
/// already uses for every other native object.
/// </para>
/// </summary>
internal static class ProfileDescriptorJson
{
    internal static string ToServerJson(ProfileDescriptor descriptor)
    {
        if (descriptor == null) return null;

        // The engine's own pipeline is the only form the server route accepts:
        // /raid/profile/scavsave deserializes ScavSaveRequestData, whose Info.Side
        // is a string, while a plain JsonConvert writes the enum as a number.
        var engineJson = TryEngineJson(descriptor);
        if (!string.IsNullOrEmpty(engineJson)) return NormalizeForServer(engineJson);

        if (!InteropAvailable())
        {
            return NormalizeForServer(NativeNewtonsoftJson.Serialize(descriptor.Cast<Il2CppSystem.Object>()));
        }

        return NormalizeForServer(SerializeThroughInterop(descriptor));
    }

    /// <summary>
    /// SPT deserializes this body into PmcData, whose Info.Side is a string
    /// ("Usec"/"Bear"/"Savage" - exactly what its own profile files contain),
    /// while the engine writes the enum as a number. Writing the number made the
    /// server answer "The JSON value could not be converted to System.String.
    /// Path: $.Info.Side" and the scav save was lost.
    /// <para>
    /// Only the Side inside the top level Info object is rewritten; later
    /// occurrences (inventory data) keep their numeric form.
    /// </para>
    /// </summary>
    private static string NormalizeForServer(string json) => NormalizeInfoType(NormalizePlayerSide(json));

    /// <summary>
    /// SPT's Tables.Info declares Type as a string as well, while the engine writes
    /// the enum as a number. The server answered
    /// "The JSON value could not be converted to System.String. Path: $.Info.Type"
    /// for /raid/profile/scavsave, which dropped the whole scav save. The numeric
    /// property is removed instead of renamed: SPT's own profiles leave Type null,
    /// so the server stores exactly what it writes itself.
    /// </summary>
    private static string NormalizeInfoType(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        const string infoMarker = "\"Info\":{";
        const string typeMarker = "\"Type\":";

        var info = json.IndexOf(infoMarker, StringComparison.Ordinal);
        if (info < 0) return json;

        // Info writes its scalars before any nested object, so the first Type
        // marker after Info belongs to it. Bound the search anyway.
        var type = json.IndexOf(typeMarker, info + infoMarker.Length, StringComparison.Ordinal);
        if (type < 0 || type - info > 2000) return json;

        var valueStart = type + typeMarker.Length;
        var valueEnd = valueStart;
        while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '-')) valueEnd++;
        if (valueEnd == valueStart) return json; // already a string or null, leave it alone

        var removeEnd = valueEnd;
        if (removeEnd < json.Length && json[removeEnd] == ',')
        {
            removeEnd++;
        }
        else
        {
            var removeStart = type;
            while (removeStart > 0 && char.IsWhiteSpace(json[removeStart - 1])) removeStart--;
            if (removeStart == 0 || json[removeStart - 1] != ',') return json;
            type = removeStart - 1;
        }

        FikaGlobals.LogInfo($"Dropped the numeric Info.Type '{json[valueStart..valueEnd]}' from the scav save payload");
        return json[..type] + json[removeEnd..];
    }

    private static string NormalizePlayerSide(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        const string infoMarker = "\"Info\":{";
        const string sideMarker = "\"Side\":";

        var info = json.IndexOf(infoMarker, StringComparison.Ordinal);
        if (info < 0) return json;

        var side = json.IndexOf(sideMarker, info + infoMarker.Length, StringComparison.Ordinal);
        if (side < 0) return json;

        var valueStart = side + sideMarker.Length;
        var valueEnd = valueStart;
        while (valueEnd < json.Length && (char.IsDigit(json[valueEnd]) || json[valueEnd] == '-')) valueEnd++;
        if (valueEnd == valueStart) return json;

        var value = json.Substring(valueStart, valueEnd - valueStart);
        var name = value switch
        {
            "1" => "Usec",
            "2" => "Bear",
            "4" => "Savage",
            _ => null
        };

        if (name == null)
        {
            FikaGlobals.LogWarning($"Unknown EPlayerSide value '{value}' in the profile payload, sending it unchanged");
            return json;
        }

        return json[..valueStart] + "\"" + name + "\"" + json[valueEnd..];
    }

    /// <summary>
    /// Runs the engine's <c>JsonExtensions.ToUnparsedData&lt;T&gt;(T, JsonConverter[] = null)</c>
    /// and returns its JToken text. The method is generic and takes a converter
    /// array whose type cannot be named while a managed Newtonsoft.Json shadows
    /// the IL2CPP proxy, so it is reached through reflection and the JToken is
    /// read natively.
    /// </summary>
    private static string TryEngineJson(ProfileDescriptor descriptor)
    {
        try
        {
            var extensions = typeof(ProfileDescriptor).Assembly.GetType("JsonExtensions");
            var method = extensions?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == "ToUnparsedData" && x.IsGenericMethodDefinition);
            if (method == null)
            {
                FikaGlobals.LogWarning("JsonExtensions.ToUnparsedData was not found, serializing the profile directly");
                return null;
            }

            var unparsed = method.MakeGenericMethod(typeof(ProfileDescriptor))
                .Invoke(null, new object[] { descriptor, null });
            if (unparsed is not Il2CppObjectBase native) return null;

            var token = NativeIl2Cpp.ReadMember(native.Pointer, "JToken");
            if (token == IntPtr.Zero)
            {
                FikaGlobals.LogWarning("The engine profile serialization produced no JToken, serializing the profile directly");
                return null;
            }

            var jToken = NativeIl2Cpp.FindClass("Newtonsoft.Json", "Newtonsoft.Json.Linq", "JToken");
            return NativeIl2Cpp.ToManagedString(
                NativeIl2Cpp.Invoke(NativeIl2Cpp.FindMethod(jToken, "ToString", 0), token));
        }
        catch (Exception exception)
        {
            FikaGlobals.LogWarning($"The engine profile serialization failed ({exception.GetBaseException().Message}), " +
                "falling back to a direct serialization");
            return null;
        }
    }

    /// <summary>
    /// True while Newtonsoft.Json still resolves to the IL2CPP interop proxy.
    /// The shadowing managed assembly is not an Il2CppObjectBase.
    /// </summary>
    internal static bool InteropAvailable()
    {
        try
        {
            return typeof(Il2CppObjectBase).IsAssignableFrom(typeof(Newtonsoft.Json.JsonConverter));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Kept in its own non-inlined method: with a shadowing assembly the linkage
    /// failure surfaces while this method is compiled, and a try/catch in the
    /// caller could not observe it otherwise.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string SerializeThroughInterop(ProfileDescriptor descriptor) =>
        descriptor.ToUnparsedData(
            new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Newtonsoft.Json.JsonConverter>(0))
            .JToken.ToString();
}
