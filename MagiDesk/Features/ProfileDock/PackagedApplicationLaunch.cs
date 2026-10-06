using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace MagiDesk.Features.ProfileDock;

internal static class PackagedApplicationLaunch
{
    // Called on the launch worker only. Keep shortcuts intact; repair raw WindowsApps EXE entries.
    internal static string? Resolve(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) return null;
        var directory = new DirectoryInfo(Path.GetDirectoryName(executable)!);
        while (directory.Parent is { } parent)
        {
            if (parent.Name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                string manifest = Path.Combine(directory.FullName, "AppxManifest.xml");
                if (!File.Exists(manifest)) return null;
                using var reader = XmlReader.Create(manifest, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024,
                });
                return ResolveManifest(XDocument.Load(reader), directory.Name,
                    Path.GetRelativePath(directory.FullName, executable));
            }
            directory = parent;
        }
        return null;
    }

    internal static string? ResolveManifest(XDocument manifest, string packageDirectory, string relativeExecutable)
    {
        // Package full name: Name_Version_Architecture_ResourceId_PublisherId.
        string[] parts = packageDirectory.Split('_');
        if (parts.Length != 5 || !Version.TryParse(parts[1], out _) ||
            parts[4].Length != 13 || !parts[4].All(char.IsAsciiLetterOrDigit)) return null;
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = manifest.Root?.Element(ns + "Identity");
        if ((string?)identity?.Attribute("Name") != parts[0]) return null;
        var matches = manifest.Root?.Element(ns + "Applications")?.Elements(ns + "Application")
            .Where(a => string.Equals(((string?)a.Attribute("Executable"))?.Replace('/', '\\'),
                relativeExecutable.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches is not { Length: 1 }) return null; // Never guess between multiple registered entry points.
        string? id = (string?)matches[0].Attribute("Id");
        if (string.IsNullOrEmpty(id) || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) return null;
        if (!parts[0].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) return null;
        return parts[0] + "_" + parts[4] + "!" + id;
    }
}
