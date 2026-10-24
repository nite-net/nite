[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(Nite.Dev.NiteHotReload))]

namespace Nite.Dev;

/// <summary>
/// Called by the .NET runtime after <c>dotnet watch</c> applies a C# hot reload inside the running
/// app. The dev server forwards it to the browser as a <c>nite:server-update</c> event, so a page
/// can re-fetch data from the changed API without reloading and losing client state.
/// </summary>
internal static class NiteHotReload
{
    public static event Action<Type[]?>? Updated;

    public static void ClearCache(Type[]? updatedTypes)
    {
    }

    public static void UpdateApplication(Type[]? updatedTypes) => Updated?.Invoke(updatedTypes);
}
