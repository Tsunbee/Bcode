using System.Text;

namespace BcodeViewer.App.Settings;

/// <summary>
/// A whole-file scaffold — the third kind of template BcodeViewer carries, alongside inline
/// snippets (typed by prefix, see <see cref="HintSnippet"/>) and the team's shared snippet
/// packs. This one answers a different question: not "what goes here in the file I'm
/// editing" but "what does a new Dir controller / report / lookup even start out as".
///
/// A template is just a file on disk — no metadata format to learn, no registry. Copy a
/// known-good controller into the templates folder, replace the parts that vary with
/// <c>${...}</c> placeholders, done. That matters for adoption: the people who know what a
/// good starting file looks like are not the people who want to edit JSON.
/// </summary>
public class FileTemplate
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    /// <summary>True for templates read from the team's shared folder — shown in the picker
    /// so it's clear which ones are the project's standard and which are someone's own.</summary>
    public bool IsShared { get; init; }

    /// <summary>What the Save dialog opens with: the template's own file name, which is
    /// usually close to what the new file should be called anyway.</summary>
    public string SuggestedFileName => System.IO.Path.GetFileName(Path);

    public string ReadContent()
    {
        try { return File.ReadAllText(Path); }
        catch (Exception ex) { return $"<!-- Không đọc được template: {ex.Message} -->"; }
    }

    /// <summary>
    /// Substitutes the placeholders. Kept to the handful that are actually knowable at
    /// creation time — anything more would be a template language, and the moment a
    /// template needs logic it should be a snippet with tabstops instead, where the person
    /// filling it in can see what they're filling in.
    /// </summary>
    public string Render(string targetPath, string projectName)
    {
        var fileName = System.IO.Path.GetFileName(targetPath);
        var bareName = System.IO.Path.GetFileNameWithoutExtension(targetPath);
        var now = DateTime.Now;

        return ReadContent()
            .Replace("${FileName}", fileName)
            .Replace("${Name}", bareName)
            .Replace("${Project}", projectName)
            .Replace("${User}", Environment.UserName)
            .Replace("${Machine}", Environment.MachineName)
            .Replace("${Date}", now.ToString("dd/MM/yyyy"))
            .Replace("${Time}", now.ToString("HH:mm"))
            .Replace("${DateTime}", now.ToString("dd/MM/yyyy HH:mm"))
            .Replace("${Guid}", Guid.NewGuid().ToString("N"));
    }
}

public static class FileTemplateStore
{
    /// <summary>Per-machine templates. A plain folder on purpose — "put a file here" is the
    /// whole interface.</summary>
    public static string PersonalFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "templates");

    /// <summary>Team templates live under <c>files\</c> inside the shared folder, keeping
    /// them separate from the <c>*.code-snippets</c> packs that sit at its root.</summary>
    public static string? SharedFolder(string? sharedTemplatePath) =>
        string.IsNullOrWhiteSpace(sharedTemplatePath) ? null : Path.Combine(sharedTemplatePath, "files");

    /// <summary>
    /// Personal templates first, then the team's. A name collision leaves both visible
    /// rather than one shadowing the other — the picker shows where each came from, and
    /// silently preferring one would be the kind of thing nobody discovers until it has
    /// already produced the wrong file.
    /// </summary>
    public static List<FileTemplate> Load(string? sharedTemplatePath)
    {
        var result = new List<FileTemplate>();
        SeedPersonalFolderIfEmpty();

        result.AddRange(Enumerate(PersonalFolder, isShared: false));
        var shared = SharedFolder(sharedTemplatePath);
        if (shared is not null) result.AddRange(Enumerate(shared, isShared: true));

        return result;
    }

    private static IEnumerable<FileTemplate> Enumerate(string folder, bool isShared)
    {
        List<string> files;
        try
        {
            if (!Directory.Exists(folder)) return Array.Empty<FileTemplate>();
            files = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly).OrderBy(f => f).ToList();
        }
        catch
        {
            return Array.Empty<FileTemplate>(); // share unreachable — personal templates still list
        }

        return files.Select(f => new FileTemplate
        {
            Name = Path.GetFileName(f),
            Path = f,
            IsShared = isShared,
        });
    }

    /// <summary>
    /// Creates the folder with one worked example the first time. Without it "New from
    /// Template" opens onto nothing and reads like a broken feature rather than an empty
    /// one — and the example is also where the placeholder list is documented in a place
    /// someone will actually look.
    /// </summary>
    private static void SeedPersonalFolderIfEmpty()
    {
        try
        {
            if (Directory.Exists(PersonalFolder) && Directory.EnumerateFileSystemEntries(PersonalFolder).Any())
                return;

            Directory.CreateDirectory(PersonalFolder);
            var sample = new StringBuilder()
                .AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>")
                .AppendLine("<!-- ${FileName} — tạo bởi ${User} lúc ${DateTime} (dự án ${Project}) -->")
                .AppendLine("<!--")
                .AppendLine("  Template file mẫu của BcodeViewer.")
                .AppendLine("  Chép 1 file controller chuẩn vào thư mục này rồi thay các chỗ thay đổi bằng:")
                .AppendLine("    ${FileName}  ${Name}  ${Project}  ${User}  ${Machine}")
                .AppendLine("    ${Date}  ${Time}  ${DateTime}  ${Guid}")
                .AppendLine("  File > New from Template (Ctrl+N) sẽ hỏi nơi lưu rồi mở file mới luôn.")
                .AppendLine("-->")
                .AppendLine("<dir id=\"${Name}\" caption=\"${Name}\">")
                .AppendLine("  <fields>")
                .AppendLine("    <field name=\"stt_rec\" caption=\"stt_rec\" type=\"string\" visible=\"false\" />")
                .AppendLine("    <field name=\"ma_dvcs\" caption=\"Đơn vị\" type=\"string\" width=\"100\" />")
                .AppendLine("  </fields>")
                .AppendLine("</dir>")
                .ToString();

            File.WriteAllText(Path.Combine(PersonalFolder, "Mau - Dir Controller.xml"), sample);
        }
        catch
        {
            // Read-only %AppData% or a locked folder — "New from Template" just shows its
            // empty-state message, which is a fine outcome for a seeding step.
        }
    }
}
