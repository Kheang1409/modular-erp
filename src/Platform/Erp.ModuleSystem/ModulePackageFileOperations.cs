namespace Erp.ModuleSystem;

internal class ModulePackageFileOperations
{
    public virtual void MoveDirectory(string source, string destination) => Directory.Move(source, destination);

    public virtual void DeleteDirectory(string path) => Directory.Delete(path, recursive: true);

    public virtual void MoveFile(string source, string destination, bool overwrite) => File.Move(source, destination, overwrite);

    public virtual void DeleteFile(string path) => File.Delete(path);
}
