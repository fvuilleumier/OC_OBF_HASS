using HassExport.Model;

namespace HassExport.Service;

/// <summary>Copie des fichiers YAML depuis le partage Samba de HA.</summary>
public class FileService(HassSettings settings)
{
    public void Export()
    {
        var root = settings.DefaultHassPath;
        if (string.IsNullOrWhiteSpace(root) || root.Contains("À_COMPLÉTER"))
        {
            Console.WriteLine("DefaultHassPath non renseigné : copie des fichiers ignorée.");
            return;
        }
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException(
                $"Partage inaccessible : {root}. Ouvre-le une fois dans l'Explorateur Windows (identifiants Samba mémorisés).");

        foreach (var e in settings.Elements)
            Copier(Path.Combine(root, e.Source), Path.Combine(settings.OutputFolder, e.Destination));

        CopierDossier(root, settings.Themes);
        CopierDossier(root, settings.Z2m);
    }

    private void CopierDossier(string root, FolderCopy f)
    {
        if (string.IsNullOrWhiteSpace(f.Source)) return;

        var src = Path.Combine(root, f.Source); // si Source est absolu, Path.Combine le garde tel quel
        var dst = Path.Combine(settings.OutputFolder, f.Destination);

        if (!Directory.Exists(src))
        {
            Console.WriteLine($"ABSENT  {src}");
            return;
        }

        var fichiers = f.Files.Count > 0
            ? f.Files.Select(n => Path.Combine(src, n))
            : Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories);

        foreach (var file in fichiers)
            Copier(file, Path.Combine(dst, Path.GetRelativePath(src, file)));
    }

    private static void Copier(string src, string dst)
    {
        if (!File.Exists(src))
        {
            Console.WriteLine($"ABSENT  {src}");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, overwrite: true);
        Console.WriteLine($"OK  {dst}");
    }
}
