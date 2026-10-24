namespace Nite;

internal static class NitePaths
{
    public static string ToUrlPath(string relativePath) => relativePath.Replace('\\', '/');
}
