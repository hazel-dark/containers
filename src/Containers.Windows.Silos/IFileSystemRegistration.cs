using Containers.Windows.Silos.DescriptionModel;

namespace Containers.Windows.Silos;

internal interface IFileSystemRegistration
{
    void Setup(FileSystemConfiguration configuration);
    void Rollback();
}