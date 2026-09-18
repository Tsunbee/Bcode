namespace Bcode.App.Services;

public class FileLookupNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public List<FileLookupNode> Children { get; } = new();
}

/// <summary>
/// Browses the FastBusiness web source tree over a UNC path (App_Data and
/// whatever it contains — the exact subfolder layout varies by site, e.g.
/// App_Data/Structure/{App,Dir,Ext,Filter,Grid,Lookup,Sys} on one real KOG
/// project, so this does not hardcode a specific convention), with an
/// "Only show *.ext" extension filter and free-text search box.
/// </summary>
public class FileLookupService
{
    public FileLookupNode BuildTree(string sourceRootPath, string extensionFilter = ".f", string? searchText = null, bool onlyShowFiltered = true)
    {
        var root = new FileLookupNode { Name = Path.GetFileName(sourceRootPath.TrimEnd('\\', '/')), FullPath = sourceRootPath, IsDirectory = true };
        if (!Directory.Exists(sourceRootPath)) return root;

        // A folder should be hidden once it has no matching descendant — that must happen
        // whenever an extension filter OR a search term is active, not just the extension
        // filter alone (otherwise a search still shows every folder in the whole tree).
        var pruneEmptyFolders = onlyShowFiltered || !string.IsNullOrWhiteSpace(searchText);
        PopulateRecursive(root, extensionFilter, searchText, onlyShowFiltered, pruneEmptyFolders);
        return root;
    }

    private void PopulateRecursive(FileLookupNode node, string extensionFilter, string? searchText, bool onlyShowFiltered, bool pruneEmptyFolders)
    {
        List<string> dirs, files;
        try
        {
            dirs = Directory.GetDirectories(node.FullPath).OrderBy(d => d).ToList();
            files = Directory.GetFiles(node.FullPath).OrderBy(f => f).ToList();
        }
        catch (Exception)
        {
            return; // permission or path issue — leave node empty rather than crash the tree build
        }

        foreach (var dir in dirs)
        {
            var childNode = new FileLookupNode { Name = Path.GetFileName(dir), FullPath = dir, IsDirectory = true };
            PopulateRecursive(childNode, extensionFilter, searchText, onlyShowFiltered, pruneEmptyFolders);
            if (!pruneEmptyFolders || childNode.Children.Count > 0)
                node.Children.Add(childNode);
        }

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (onlyShowFiltered && !string.IsNullOrEmpty(extensionFilter) && !ext.Equals(extensionFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(file);
            if (!string.IsNullOrWhiteSpace(searchText) && name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            node.Children.Add(new FileLookupNode { Name = name, FullPath = file, IsDirectory = false });
        }
    }
}
